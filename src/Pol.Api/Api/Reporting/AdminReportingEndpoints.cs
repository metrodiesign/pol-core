using System.Globalization;
using System.Text;
using System.Text.Json;
using Admins.Application;
using Api.Admins;
using Api.Iam;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Reporting.Application;

namespace Api.Reporting;

internal static class AdminReportingEndpoints
{
    private const int MaxExportRows = 100_000;
    private const int MaxExportBytes = 100 * 1024 * 1024;

    public static void MapAdminReportingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/reports/dashboard", async (
            HttpContext http,
            IAdminScope scope,
            IAdminReportingReader reporting,
            IClock clock,
            CancellationToken ct) =>
        {
            var period = ParsePeriod(http, clock, required: false);
            if (period.Error is not null)
                return period.Error;

            var merchantId = OptionalGuid(http, "merchantId");
            var result = await reporting.DashboardAsync(
                period.Value!, Access(scope), merchantId, ct);
            return Results.Ok(Dashboard(result, clock.UtcNow));
        }).RequireAuthorization("admin").RequirePermission(Keys.TxnView)
            .WithMetadata(
                Query("from", "Inclusive UTC period start; defaults to seven days before to.", false),
                Query("to", "Inclusive UTC period end; defaults to current server time.", false),
                Query("merchantId", "Optional merchant UUID within current Admin scope.", false))
            .WithTags("รายงาน")
            .WithName("GetAdminDashboard")
            .WithSummary("ข้อมูลสรุป dashboard จากธุรกรรมจริง")
            .WithDescription("สรุปยอดและจำนวนธุรกรรมตาม currency พร้อม success/failure/pending และ breakdown ตาม PSP, method, originator ค่าเริ่มต้น 7 วัน สูงสุด 31 วัน และจำกัดตาม Admin merchant scope")
            .Produces<DashboardResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/payments/transactions", async (
            HttpContext http,
            IAdminScope scope,
            IAdminReportingReader reporting,
            IClock clock,
            CancellationToken ct) =>
        {
            var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
            var filters = WithDefaultPeriod(parsed.Filters, clock.UtcNow);
            try
            {
                var result = await reporting.ListTransactionsAsync(new AdminTransactionQuery(Access(scope))
                {
                    Page = parsed.Page,
                    Limit = parsed.Limit,
                    Filters = filters,
                    Sort = parsed.Sort,
                    Search = parsed.Search,
                }, ct);
                return Results.Ok(new PagedResult<TransactionListResponse>(
                    result.Items.Select(Transaction).ToArray(),
                    result.Page, result.Limit, result.Total));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidRequestException(ex.Message, "invalid_filter");
            }
        }).RequireAuthorization("admin").RequirePermission(Keys.TxnView)
            .WithMetadata(new SfsQueryParamsMarker(100))
            .WithTags("ธุรกรรม")
            .WithName("ListAdminTransactions")
            .WithSummary("รายการธุรกรรมจาก Order และ PaymentSession")
            .WithDescription("projection แบบแบ่งหน้าจาก Order และ PaymentSession รองรับ SFS ค่าเริ่มต้นย้อนหลัง 7 วัน คืนเงินเป็น decimal string 4 ตำแหน่ง mask ข้อมูลลูกค้า และจำกัดตาม Admin merchant scope")
            .Produces<PagedResult<TransactionListResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/payments/transactions/export", async (
            HttpContext http,
            IAdminScope scope,
            IAdminReportingReader reporting,
            IClock clock,
            CancellationToken ct) =>
        {
            var period = ParsePeriod(http, clock, required: true);
            if (period.Error is not null)
                return period.Error;

            var parsed = SfsQueryParser.Parse(http.Request.Query, maxLimit: 100);
            var filters = WithoutCreatedAt(parsed.Filters).Concat(
            [
                new FilterOption("createdAt", FilterOperator.Between, Values:
                [
                    JsonSerializer.SerializeToElement(period.Value!.From.ToString("O")),
                    JsonSerializer.SerializeToElement(period.Value.To.ToString("O")),
                ]),
            ]).ToArray();
            PagedResult<AdminTransactionListItem> result;
            try
            {
                result = await reporting.ListTransactionsAsync(new AdminTransactionQuery(Access(scope))
                {
                    Page = 1,
                    Limit = MaxExportRows + 1,
                    Filters = filters,
                    Sort = parsed.Sort,
                    Search = parsed.Search,
                }, ct);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidRequestException(ex.Message, "invalid_filter");
            }

            if (result.Total > MaxExportRows)
                return ExportTooLarge();

            var csv = new StringBuilder();
            csv.AppendLine("transactionId,orderId,orderNo,merchantId,originatorId,amount,currency,method,psp,status,itemCount,createdAt,updatedAt,dataQualityCode");
            foreach (var item in result.Items)
            {
                csv.AppendLine(string.Join(',', new[]
                {
                    Csv(item.TransactionId.ToString("D")), Csv(item.OrderId.ToString("D")), Csv(item.OrderNo),
                    Csv(item.MerchantId.ToString("D")), Csv(item.OriginatorId?.ToString("D")),
                    Csv(Money(item.Amount)), Csv(item.Currency), Csv(item.Method), Csv(item.Psp), Csv(item.Status),
                    Csv(item.ItemCount.ToString(CultureInfo.InvariantCulture)),
                    Csv(item.CreatedAt.ToUniversalTime().ToString("O")),
                    Csv(item.UpdatedAt.ToUniversalTime().ToString("O")), Csv(item.DataQualityCode),
                }));
            }
            var bytes = Encoding.UTF8.GetBytes(csv.ToString());
            return bytes.Length > MaxExportBytes
                ? ExportTooLarge()
                : Results.File(bytes, "text/csv; charset=utf-8",
                    $"transactions-{period.Value.From:yyyyMMdd}-{period.Value.To:yyyyMMdd}.csv");
        }).RequireAuthorization("admin").RequirePermission(Keys.TxnExport)
            .WithMetadata(
                new SfsQueryParamsMarker(100),
                Query("from", "Inclusive UTC export start; required.", true),
                Query("to", "Inclusive UTC export end; required and at most 31 days after from.", true))
            .WithTags("ธุรกรรม")
            .WithName("ExportAdminTransactions")
            .WithSummary("ส่งออกธุรกรรมตาม query เดียวกับหน้าจอ")
            .WithDescription("ส่ง CSV จาก filter/sort/search เดียวกับรายการ ต้องระบุ from/to ช่วงไม่เกิน 31 วัน จำกัด 100,000 แถวและ 100 MiB พร้อมป้องกัน spreadsheet formula injection")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/payments/transactions/{paymentSessionId:guid}", async (
            Guid paymentSessionId,
            HttpContext http,
            IAdminScope scope,
            IAdminReportingReader reporting,
            CancellationToken ct) =>
        {
            var result = await reporting.GetTransactionAsync(paymentSessionId, Access(scope), ct);
            if (result is null)
                return Results.NotFound();
            VersionEtags.Set(http, result.Transaction.Version);
            return Results.Ok(TransactionDetail(result));
        }).RequireAuthorization("admin").RequirePermission(Keys.TxnView)
            .WithMetadata(new AdminEtagResponseMarker("200"))
            .WithTags("ธุรกรรม")
            .WithName("GetAdminTransaction")
            .WithSummary("รายละเอียดธุรกรรมและ lifecycle ที่ backend บันทึกจริง")
            .WithDescription("คืน transaction projection, Order lines, lifecycle events, capability flags และ ETag โดย mask ข้อมูลลูกค้า หากไม่พบหรือนอก Admin merchant scope -> 404")
            .Produces<TransactionDetailResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/reports/operations", async (
            HttpContext http,
            IAdminScope scope,
            IAdminReportingReader reporting,
            IClock clock,
            CancellationToken ct) =>
        {
            var period = ParsePeriod(http, clock, required: false);
            if (period.Error is not null)
                return period.Error;
            var result = await reporting.DashboardAsync(
                period.Value!, Access(scope), OptionalGuid(http, "merchantId"), ct);
            return Results.Ok(new OperationsReportResponse(Dashboard(result, clock.UtcNow), []));
        }).RequireAuthorization("admin").RequirePermission(Keys.TxnView)
            .WithMetadata(
                Query("from", "Inclusive UTC period start; defaults to seven days before to.", false),
                Query("to", "Inclusive UTC period end; defaults to current server time.", false),
                Query("merchantId", "Optional merchant UUID within current Admin scope.", false))
            .WithTags("รายงาน")
            .WithName("GetOperationsReport")
            .WithSummary("รายงานปฏิบัติการจากธุรกรรมจริง")
            .WithDescription("คืน summary ชุดเดียวกับ dashboard สำหรับช่วงเวลาที่เลือก ค่าเริ่มต้น 7 วัน สูงสุด 31 วัน และกรอง merchantId ภายใน Admin scope ได้")
            .Produces<OperationsReportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapGet("/reports/operations/export", async (
            HttpContext http,
            IAdminScope scope,
            IAdminReportingReader reporting,
            IClock clock,
            CancellationToken ct) =>
        {
            var period = ParsePeriod(http, clock, required: true);
            if (period.Error is not null)
                return period.Error;
            var selectedPeriod = period.Value!;
            var result = await reporting.DashboardAsync(
                selectedPeriod, Access(scope), OptionalGuid(http, "merchantId"), ct);
            var csv = OperationsCsv(result);
            var bytes = Encoding.UTF8.GetBytes(csv);
            return bytes.Length > MaxExportBytes
                ? ExportTooLarge()
                : Results.File(bytes, "text/csv; charset=utf-8",
                    $"operations-{selectedPeriod.From:yyyyMMdd}-{selectedPeriod.To:yyyyMMdd}.csv");
        }).RequireAuthorization("admin").RequirePermission(Keys.TxnExport)
            .WithMetadata(
                Query("from", "Inclusive UTC export start; required.", true),
                Query("to", "Inclusive UTC export end; required and at most 31 days after from.", true),
                Query("merchantId", "Optional merchant UUID within current Admin scope.", false))
            .WithTags("รายงาน")
            .WithName("ExportOperationsReport")
            .WithSummary("ส่งออกรายงานปฏิบัติการ")
            .WithDescription("ส่ง CSV ของ totals และ breakdown ตาม PSP, method, originator ต้องระบุ from/to ช่วงไม่เกิน 31 วัน จำกัด 100 MiB พร้อมป้องกัน spreadsheet formula injection")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static ReportingAccess Access(IAdminScope scope) =>
        new(scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

    private static IReadOnlyList<FilterOption> WithDefaultPeriod(
        IReadOnlyList<FilterOption> filters,
        DateTime now) =>
        filters.Any(x => x.Field == "createdAt")
            ? filters
            : filters.Concat(
            [
                new FilterOption("createdAt", FilterOperator.Between, Values:
                [
                    JsonSerializer.SerializeToElement((now - TimeSpan.FromDays(7)).ToString("O")),
                    JsonSerializer.SerializeToElement(now.ToString("O")),
                ]),
            ]).ToArray();

    private static IEnumerable<FilterOption> WithoutCreatedAt(IReadOnlyList<FilterOption> filters) =>
        filters.Where(x => x.Field != "createdAt");

    private static PeriodParse ParsePeriod(HttpContext http, IClock clock, bool required)
    {
        var rawFrom = http.Request.Query["from"].ToString();
        var rawTo = http.Request.Query["to"].ToString();
        if (required && (string.IsNullOrWhiteSpace(rawFrom) || string.IsNullOrWhiteSpace(rawTo)))
            return new(null, Problem(StatusCodes.Status400BadRequest, "invalid_period"));

        var to = string.IsNullOrWhiteSpace(rawTo) ? clock.UtcNow : ParseInstant(rawTo);
        var from = string.IsNullOrWhiteSpace(rawFrom) ? to - TimeSpan.FromDays(7) : ParseInstant(rawFrom);
        if (from is null || to is null || to < from)
            return new(null, Problem(StatusCodes.Status400BadRequest, "invalid_period"));
        if (to.Value - from.Value > TimeSpan.FromDays(31))
            return new(null, Problem(StatusCodes.Status422UnprocessableEntity, "query_too_broad"));
        return new(new ReportingPeriod(from.Value, to.Value), null);
    }

    private static DateTime? ParseInstant(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;

    private static Guid? OptionalGuid(HttpContext http, string name)
    {
        var raw = http.Request.Query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (!Guid.TryParse(raw, out var value) || value == Guid.Empty)
            throw new InvalidRequestException($"{name} must be a non-empty UUID.", "invalid_filter");
        return value;
    }

    private static IResult Problem(int status, string code) => Results.Problem(
        statusCode: status,
        title: status == StatusCodes.Status422UnprocessableEntity ? "Query is too broad." : "Invalid period.",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult ExportTooLarge() => Results.Problem(
        statusCode: StatusCodes.Status422UnprocessableEntity,
        title: "Export contains too much data.",
        extensions: new Dictionary<string, object?> { ["code"] = "export_too_large" });

    private static RawQueryParamMarker Query(string name, string description, bool required) =>
        new(name, description, required);

    private static DashboardResponse Dashboard(AdminDashboardResult value, DateTime generatedAt) => new(
        new PeriodResponse(value.Period.From, value.Period.To),
        value.Totals.Select(x => new CurrencyTotalResponse(Money(x.Amount), x.Currency, x.Count)).ToArray(),
        value.TransactionCount, value.SuccessCount, value.FailureCount, value.PendingCount,
        value.ByPsp.Select(Breakdown).ToArray(), value.ByMethod.Select(Breakdown).ToArray(),
        value.ByOriginator.Select(Breakdown).ToArray(), generatedAt);

    private static BreakdownResponse Breakdown(ReportingBreakdown value) =>
        new(value.Key, value.Label, Money(value.Amount), value.Currency, value.Count);

    private static TransactionListResponse Transaction(AdminTransactionListItem value) => new(
        value.TransactionId, value.OrderId, value.OrderNo, value.MerchantId, value.OriginatorId,
        new MoneyResponse(Money(value.Amount), value.Currency), value.Method, value.Psp, value.Status,
        value.SessionStatus, value.OrderStatus, value.ItemCount, MaskName(value.CustomerName),
        MaskReference(value.CustomerEmail, value.CustomerPhone), value.ExternalChargeId,
        value.CreatedAt, value.UpdatedAt, value.DataQualityCode, value.Version);

    private static TransactionDetailResponse TransactionDetail(AdminTransactionDetail value) => new(
        Transaction(value.Transaction),
        value.Lines.Select(x => new TransactionLineResponse(
            x.ProductCode, x.VariantCode, x.VariantName, x.Quantity,
            new MoneyResponse(Money(x.UnitAmount), x.Currency),
            new MoneyResponse(Money(x.DiscountAmount), x.Currency))).ToArray(),
        value.Lifecycle.Select(x => new LifecycleResponse(x.EventId, x.Status, x.At)).ToArray(),
        TransactionProjectionRules.Capabilities(value.Transaction.OrderStatus)
            .Select(x => new CapabilityResponse(x.Code, x.Available, x.RequiresApproval, x.ReasonCode)).ToArray());

    private static string OperationsCsv(AdminDashboardResult value)
    {
        var csv = new StringBuilder();
        csv.AppendLine("section,key,label,amount,currency,count");
        foreach (var item in value.Totals)
            csv.AppendLine(string.Join(',', Csv("total"), Csv(item.Currency), Csv(item.Currency),
                Csv(Money(item.Amount)), Csv(item.Currency), Csv(item.Count.ToString(CultureInfo.InvariantCulture))));
        AppendBreakdown(csv, "psp", value.ByPsp);
        AppendBreakdown(csv, "method", value.ByMethod);
        AppendBreakdown(csv, "originator", value.ByOriginator);
        return csv.ToString();
    }

    private static void AppendBreakdown(
        StringBuilder csv,
        string section,
        IReadOnlyList<ReportingBreakdown> values)
    {
        foreach (var item in values)
            csv.AppendLine(string.Join(',', Csv(section), Csv(item.Key), Csv(item.Label),
                Csv(Money(item.Amount)), Csv(item.Currency), Csv(item.Count.ToString(CultureInfo.InvariantCulture))));
    }

    private static string Money(decimal value) => value.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && "=+-@".Contains(value[0]))
            value = "'" + value;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string MaskName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "***" : $"{value.Trim()[0]}***";

    private static string MaskReference(string? email, string phone)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            var at = email.IndexOf('@');
            return at > 0 ? $"{email[0]}***{email[at..]}" : "***";
        }
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? $"***{digits[^4..]}" : "***";
    }

    private sealed record PeriodParse(ReportingPeriod? Value, IResult? Error);
}

internal sealed record PeriodResponse(DateTime From, DateTime To);
internal sealed record MoneyResponse(string Amount, string Currency);
internal sealed record CurrencyTotalResponse(string Amount, string Currency, long Count);
internal sealed record BreakdownResponse(string Key, string Label, string Amount, string Currency, long Count);
internal sealed record DashboardResponse(
    PeriodResponse Period,
    IReadOnlyList<CurrencyTotalResponse> Totals,
    long TransactionCount,
    long SuccessCount,
    long FailureCount,
    long PendingCount,
    IReadOnlyList<BreakdownResponse> ByPsp,
    IReadOnlyList<BreakdownResponse> ByMethod,
    IReadOnlyList<BreakdownResponse> ByOriginator,
    DateTime GeneratedAt);
internal sealed record TransactionListResponse(
    Guid TransactionId,
    Guid OrderId,
    string OrderNo,
    Guid MerchantId,
    Guid? OriginatorId,
    MoneyResponse Amount,
    string Method,
    string Psp,
    string Status,
    string SessionStatus,
    string OrderStatus,
    int ItemCount,
    string CustomerName,
    string CustomerReference,
    string? ExternalChargeId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? DataQualityCode,
    long Version);
internal sealed record TransactionLineResponse(
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    MoneyResponse UnitPrice,
    MoneyResponse Discount);
internal sealed record LifecycleResponse(Guid EventId, string Status, DateTime At);
internal sealed record CapabilityResponse(string Code, bool Available, bool RequiresApproval, string? ReasonCode);
internal sealed record TransactionDetailResponse(
    TransactionListResponse Transaction,
    IReadOnlyList<TransactionLineResponse> Lines,
    IReadOnlyList<LifecycleResponse> Lifecycle,
    IReadOnlyList<CapabilityResponse> Capabilities);
internal sealed record OperationsReportResponse(
    DashboardResponse Summary,
    IReadOnlyList<string> UnavailableSections);
