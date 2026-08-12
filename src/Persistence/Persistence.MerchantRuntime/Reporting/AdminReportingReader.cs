using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Payments.Domain;
using Payments.Domain.Psp;
using Reporting.Application;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Persistence.MerchantRuntime.Reporting;

internal sealed class AdminReportingReader(MerchantRuntimeDbContext db) : IAdminReportingReader
{
    public async Task<AdminDashboardResult> DashboardAsync(
        ReportingPeriod period,
        ReportingAccess access,
        Guid? merchantId,
        CancellationToken cancellationToken)
    {
        if (merchantId is { } selected && !access.Allows(selected))
            return EmptyDashboard(period);

        var source = Source(access, merchantId)
            .Where(x => x.CreatedAt >= period.From && x.CreatedAt <= period.To);

        var totals = await ReadAsync(source
            .GroupBy(x => x.Currency)
            .OrderBy(group => group.Key)
            .Select(group => new CurrencyTotal(
                group.Key, group.Sum(x => x.Amount), group.LongCount())), cancellationToken);

        var counts = await PlatformReadGuard.ReadAsync(ct => source
            .GroupBy(_ => 1)
            .Select(group => new StatusCounts(
                group.LongCount(),
                group.LongCount(x => x.SessionStatus == SessionStatus.Paid),
                group.LongCount(x => x.SessionStatus == SessionStatus.Failed
                    || x.SessionStatus == SessionStatus.Expired),
                group.LongCount(x => x.SessionStatus == SessionStatus.Created
                    || x.SessionStatus == SessionStatus.Redirected)))
            .SingleOrDefaultAsync(ct), cancellationToken) ?? new StatusCounts(0, 0, 0, 0);

        var byPsp = await ReadAsync(source
            .GroupBy(x => new { x.Psp, x.Currency })
            .OrderByDescending(group => group.Sum(x => x.Amount))
            .Select(group => new PspBreakdown(
                group.Key.Psp, group.Key.Currency,
                group.Sum(x => x.Amount), group.LongCount())), cancellationToken);

        var byMethod = await ReadAsync(source
            .GroupBy(x => new { x.Method, x.Currency })
            .OrderByDescending(group => group.Sum(x => x.Amount))
            .Select(group => new RawBreakdown(
                group.Key.Method, group.Key.Method, group.Key.Currency,
                group.Sum(x => x.Amount), group.LongCount())), cancellationToken);

        var byOriginator = await ReadAsync(source
            .GroupBy(x => new { x.OriginatorId, x.OriginatorCode, x.OriginatorName, x.Currency })
            .OrderByDescending(group => group.Sum(x => x.Amount))
            .Select(group => new OriginatorBreakdown(
                group.Key.OriginatorId,
                group.Key.OriginatorName ?? group.Key.OriginatorCode,
                group.Key.Currency, group.Sum(x => x.Amount), group.LongCount())), cancellationToken);

        return new AdminDashboardResult(
            period,
            totals,
            counts.TransactionCount,
            counts.SuccessCount,
            counts.FailureCount,
            counts.PendingCount,
            byPsp.Select(x => new ReportingBreakdown(
                x.Psp.ToCode(), x.Psp.ToCode(), x.Currency, x.Amount, x.Count)).ToArray(),
            byMethod.Select(x => Breakdown(x, x.Key, x.Label)).ToArray(),
            byOriginator.Select(x => new ReportingBreakdown(
                x.OriginatorId?.ToString("D") ?? "unassigned", x.Label ?? "unassigned",
                x.Currency, x.Amount, x.Count)).ToArray());
    }

    public async Task<PagedResult<AdminTransactionListItem>> ListTransactionsAsync(
        AdminTransactionQuery query,
        CancellationToken cancellationToken)
    {
        var source = ApplyFilters(Source(query.Access, merchantId: null), query.Filters);
        source = ApplySearch(source, query.Search);

        var total = await PlatformReadGuard.ReadAsync(
            ct => source.LongCountAsync(ct), cancellationToken);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var rows = await PlatformReadGuard.ReadAsync(ct => ApplySort(source, query.Sort)
            .Skip(skip)
            .Take(query.Limit)
            .ToListAsync(ct), cancellationToken);

        var itemCounts = new Dictionary<(Guid OrderId, Guid MerchantId), int>();
        if (rows.Count > 0)
        {
            var orderIds = rows.Select(x => x.OrderId).Distinct().ToArray();
            var merchantIds = rows.Select(x => x.MerchantId).Distinct().ToArray();
            var counts = await ReadAsync(
                db.OrderItems.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(x => orderIds.Contains(x.OrderId) && merchantIds.Contains(x.MerchantId))
                .GroupBy(x => new { x.OrderId, x.MerchantId })
                .Select(group => new ItemCountRow(
                    group.Key.OrderId, group.Key.MerchantId, group.Count())), cancellationToken);
            foreach (var count in counts)
                itemCounts[(count.OrderId, count.MerchantId)] = count.Count;
        }

        return new PagedResult<AdminTransactionListItem>(
            rows.Select(row => Project(row,
                itemCounts.GetValueOrDefault((row.OrderId, row.MerchantId)))).ToArray(),
            query.Page, query.Limit, total);
    }

    public async Task<AdminTransactionDetail?> GetTransactionAsync(
        Guid paymentSessionId,
        ReportingAccess access,
        CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => Source(access, merchantId: null)
            .SingleOrDefaultAsync(x => x.TransactionId == paymentSessionId, ct), cancellationToken);
        if (row is null)
            return null;

        var lines = await PlatformReadGuard.ReadAsync(ct => db.Set<global::Orders.Domain.Items.Item>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.OrderId == row.OrderId && x.MerchantId == row.MerchantId)
            .OrderBy(x => x.Id)
            .Select(x => new AdminTransactionOrderLine(
                x.ProductCode, x.VariantCode, x.VariantName, x.Quantity,
                x.UnitPrice.Amount, x.Discount.Amount, x.UnitPrice.Currency))
            .ToListAsync(ct), cancellationToken);

        var idText = paymentSessionId.ToString("D");
        var messages = await PlatformReadGuard.ReadAsync(ct => db.Set<OutboxMessage>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => x.MerchantId == row.MerchantId
                && (x.Type == PaymentPaid.EventType
                    || x.Type == PaymentFailed.EventType
                    || x.Type == PaymentExpired.EventType)
                && x.Payload.Contains(idText))
            .OrderBy(x => x.OccurredAt)
            .Select(x => new OutboxProjection(x.Type, x.Payload))
            .ToListAsync(ct), cancellationToken);

        return new AdminTransactionDetail(
            Project(row, lines.Count), lines, ParseLifecycle(messages, paymentSessionId));
    }

    private IQueryable<TransactionRow> Source(ReportingAccess access, Guid? merchantId)
    {
        var sessions = db.Set<Session>().IgnoreQueryFilters().AsNoTracking();
        var orders = db.Set<Order>().IgnoreQueryFilters().AsNoTracking();
        var originators = db.Set<Originator>().IgnoreQueryFilters().AsNoTracking();

        var source =
            from session in sessions
            join order in orders
                on new { session.MerchantId, session.OrderId }
                equals new { order.MerchantId, OrderId = order.Id }
            join originator in originators
                on new { order.MerchantId, OriginatorId = order.OriginatorId }
                equals new { originator.MerchantId, OriginatorId = (Guid?)originator.Id }
                into originatorGroup
            from originator in originatorGroup.DefaultIfEmpty()
            select new TransactionRow
            {
                TransactionId = session.Id,
                OrderId = order.Id,
                OrderNo = order.OrderNo,
                MerchantId = order.MerchantId,
                OriginatorId = order.OriginatorId,
                OriginatorCode = originator == null ? null : originator.Code,
                OriginatorName = originator == null ? null : originator.Name,
                Amount = session.Amount.Amount,
                Currency = session.Amount.Currency,
                Method = session.Method,
                Psp = session.Psp,
                SessionStatus = session.Status,
                OrderStatus = order.Status,
                CustomerName = order.CustomerName,
                CustomerPhone = order.CustomerPhone,
                CustomerEmail = order.CustomerEmail,
                ExternalChargeId = session.PspExternalChargeId,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                Version = session.Version,
            };

        if (!access.IsUnrestricted)
            source = source.Where(x => access.MerchantIds.Contains(x.MerchantId));
        if (merchantId is { } selected)
            source = source.Where(x => x.MerchantId == selected);
        return source;
    }

    private static IQueryable<TransactionRow> ApplyFilters(
        IQueryable<TransactionRow> source,
        IReadOnlyList<FilterOption> filters)
    {
        foreach (var filter in filters)
        {
            source = (filter.Field, filter.Operator) switch
            {
                ("status", FilterOperator.Equals) => Status(source, String(filter.Value)),
                ("status", FilterOperator.In) when filter.Values is { Length: > 0 } =>
                    Status(source, filter.Values.Select(x => String(x)).ToArray()),
                ("method", FilterOperator.Equals) =>
                    source.Where(x => x.Method == Method(String(filter.Value))),
                ("method", FilterOperator.In) when filter.Values is { Length: > 0 } =>
                    Methods(source, filter.Values),
                ("psp", FilterOperator.Equals) =>
                    source.Where(x => x.Psp == Codes.FromCode(String(filter.Value).ToLowerInvariant())),
                ("psp", FilterOperator.In) when filter.Values is { Length: > 0 } =>
                    Psps(source, filter.Values),
                ("merchantId", FilterOperator.Equals) =>
                    source.Where(x => x.MerchantId == GuidValue(filter.Value)),
                ("merchantId", FilterOperator.In) when filter.Values is { Length: > 0 } =>
                    MerchantIds(source, filter.Values),
                ("originatorId", FilterOperator.Equals) =>
                    source.Where(x => x.OriginatorId == GuidValue(filter.Value)),
                ("originatorId", FilterOperator.In) when filter.Values is { Length: > 0 } =>
                    OriginatorIds(source, filter.Values),
                ("originatorId", FilterOperator.IsNull) => source.Where(x => x.OriginatorId == null),
                ("createdAt", FilterOperator.GreaterThanOrEqual) =>
                    source.Where(x => x.CreatedAt >= Instant(filter.Value)),
                ("createdAt", FilterOperator.LessThanOrEqual) =>
                    source.Where(x => x.CreatedAt <= Instant(filter.Value)),
                ("createdAt", FilterOperator.Between) when filter.Values is { Length: 2 } =>
                    Between(source, filter.Values),
                _ => throw new ArgumentException(
                    $"Filter {filter.Field}/{filter.Operator} is not supported."),
            };
        }
        return source;
    }

    private static IQueryable<TransactionRow> ApplySearch(
        IQueryable<TransactionRow> source,
        SearchOption? search)
    {
        if (string.IsNullOrWhiteSpace(search?.Query))
            return source;

        var fields = search.Fields is { Length: > 0 }
            ? search.Fields.ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(["transactionId", "orderNo", "externalChargeId", "customer"], StringComparer.Ordinal);
        if (fields.Any(x => x is not ("transactionId" or "orderNo" or "externalChargeId" or "customer")))
            throw new ArgumentException("Search contains an unsupported field.");

        var term = search.Query.Trim();
        var pattern = $"%{SfsLike.Escape(term)}%";
        var transactionId = Guid.TryParse(term, out var parsed) ? parsed : Guid.Empty;
        var byId = fields.Contains("transactionId");
        var byOrder = fields.Contains("orderNo");
        var byExternal = fields.Contains("externalChargeId");
        var byCustomer = fields.Contains("customer");
        return source.Where(x =>
            (byId && transactionId != Guid.Empty && x.TransactionId == transactionId)
            || (byOrder && EF.Functions.Like(x.OrderNo, pattern, "\\"))
            || (byExternal && x.ExternalChargeId != null
                && EF.Functions.Like(x.ExternalChargeId, pattern, "\\"))
            || (byCustomer && (EF.Functions.Like(x.CustomerName, pattern, "\\")
                || (x.CustomerEmail != null && EF.Functions.Like(x.CustomerEmail, pattern, "\\")))));
    }

    private static IOrderedQueryable<TransactionRow> ApplySort(
        IQueryable<TransactionRow> source,
        IReadOnlyList<SortOption> sort)
    {
        IOrderedQueryable<TransactionRow>? ordered = null;
        foreach (var option in sort)
        {
            var asc = option.Order == SortDirection.Asc;
            ordered = (option.Field, ordered is null) switch
            {
                ("createdAt", true) => asc ? source.OrderBy(x => x.CreatedAt) : source.OrderByDescending(x => x.CreatedAt),
                ("createdAt", false) => asc ? ordered!.ThenBy(x => x.CreatedAt) : ordered!.ThenByDescending(x => x.CreatedAt),
                ("updatedAt", true) => asc ? source.OrderBy(x => x.UpdatedAt) : source.OrderByDescending(x => x.UpdatedAt),
                ("updatedAt", false) => asc ? ordered!.ThenBy(x => x.UpdatedAt) : ordered!.ThenByDescending(x => x.UpdatedAt),
                ("transactionId", true) => asc ? source.OrderBy(x => x.TransactionId) : source.OrderByDescending(x => x.TransactionId),
                ("transactionId", false) => asc ? ordered!.ThenBy(x => x.TransactionId) : ordered!.ThenByDescending(x => x.TransactionId),
                ("orderNo", true) => asc ? source.OrderBy(x => x.OrderNo) : source.OrderByDescending(x => x.OrderNo),
                ("orderNo", false) => asc ? ordered!.ThenBy(x => x.OrderNo) : ordered!.ThenByDescending(x => x.OrderNo),
                ("amount", true) => asc ? source.OrderBy(x => x.Amount) : source.OrderByDescending(x => x.Amount),
                ("amount", false) => asc ? ordered!.ThenBy(x => x.Amount) : ordered!.ThenByDescending(x => x.Amount),
                ("status", true) => asc ? source.OrderBy(x => x.SessionStatus) : source.OrderByDescending(x => x.SessionStatus),
                ("status", false) => asc ? ordered!.ThenBy(x => x.SessionStatus) : ordered!.ThenByDescending(x => x.SessionStatus),
                _ => throw new ArgumentException($"Sort field {option.Field} is not supported."),
            };
        }
        return ordered is null
            ? source.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.TransactionId)
            : ordered.ThenBy(x => x.TransactionId);
    }

    private static IQueryable<TransactionRow> Status(
        IQueryable<TransactionRow> source,
        params string[] values)
    {
        var normalized = values.Select(x => x.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (normalized.Any(x => x is not ("pending" or "paid" or "failed" or "expired")))
            throw new ArgumentException("Invalid transaction status.");
        return source.Where(x =>
            (normalized.Contains("pending")
                && (x.SessionStatus == SessionStatus.Created || x.SessionStatus == SessionStatus.Redirected))
            || (normalized.Contains("paid") && x.SessionStatus == SessionStatus.Paid)
            || (normalized.Contains("failed") && x.SessionStatus == SessionStatus.Failed)
            || (normalized.Contains("expired") && x.SessionStatus == SessionStatus.Expired));
    }

    private static IQueryable<TransactionRow> Between(
        IQueryable<TransactionRow> source,
        JsonElement[] values)
    {
        var from = Instant(values[0]);
        var to = Instant(values[1]);
        if (to < from)
            throw new ArgumentException("createdAt range is reversed.");
        return source.Where(x => x.CreatedAt >= from && x.CreatedAt <= to);
    }

    private static IQueryable<TransactionRow> Methods(
        IQueryable<TransactionRow> source,
        JsonElement[] raw)
    {
        var values = raw.Select(x => Method(String(x))).ToArray();
        return source.Where(x => values.Contains(x.Method));
    }

    private static IQueryable<TransactionRow> Psps(
        IQueryable<TransactionRow> source,
        JsonElement[] raw)
    {
        var values = raw.Select(x => Codes.FromCode(String(x).ToLowerInvariant())).ToArray();
        return source.Where(x => values.Contains(x.Psp));
    }

    private static IQueryable<TransactionRow> MerchantIds(
        IQueryable<TransactionRow> source,
        JsonElement[] raw)
    {
        var values = raw.Select(x => GuidValue(x)).ToArray();
        return source.Where(x => values.Contains(x.MerchantId));
    }

    private static IQueryable<TransactionRow> OriginatorIds(
        IQueryable<TransactionRow> source,
        JsonElement[] raw)
    {
        var values = raw.Select(x => GuidValue(x)).ToArray();
        return source.Where(x => x.OriginatorId != null && values.Contains(x.OriginatorId.Value));
    }

    private static AdminTransactionListItem Project(TransactionRow row, int itemCount)
    {
        var normalized = TransactionProjectionRules.Normalize(
            row.SessionStatus.ToString(), row.OrderStatus.ToString());
        return new AdminTransactionListItem(
            row.TransactionId, row.OrderId, row.OrderNo, row.MerchantId, row.OriginatorId,
            row.Amount, row.Currency, row.Method, row.Psp.ToCode(), normalized.Status,
            row.SessionStatus.ToString(), row.OrderStatus.ToString().ToLowerInvariant(), itemCount,
            row.CustomerName, row.CustomerPhone, row.CustomerEmail, row.ExternalChargeId,
            row.CreatedAt, row.UpdatedAt, normalized.DataQualityCode, row.Version);
    }

    private static IReadOnlyList<AdminTransactionLifecycleEvent> ParseLifecycle(
        IReadOnlyList<OutboxProjection> messages,
        Guid paymentSessionId)
    {
        var result = new List<AdminTransactionLifecycleEvent>();
        foreach (var message in messages)
        {
            switch (message.Type)
            {
                case PaymentPaid.EventType:
                    {
                        var value = JsonSerializer.Deserialize<PaymentPaid>(message.Payload);
                        if (value?.PaymentSessionId == paymentSessionId)
                            result.Add(new(value.EventId, "paid", value.OccurredAt));
                        break;
                    }
                case PaymentFailed.EventType:
                    {
                        var value = JsonSerializer.Deserialize<PaymentFailed>(message.Payload);
                        if (value?.PaymentSessionId == paymentSessionId)
                            result.Add(new(value.EventId, "failed", value.OccurredAt));
                        break;
                    }
                case PaymentExpired.EventType:
                    {
                        var value = JsonSerializer.Deserialize<PaymentExpired>(message.Payload);
                        if (value?.PaymentSessionId == paymentSessionId)
                            result.Add(new(value.EventId, "expired", value.OccurredAt));
                        break;
                    }
            }
        }
        return result.OrderBy(x => x.At).ThenBy(x => x.EventId).ToArray();
    }

    private static string String(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } element && element.GetString() is { } text
            ? text
            : throw new ArgumentException("Filter value must be a string.");

    private static Guid GuidValue(JsonElement? value) =>
        Guid.TryParse(String(value), out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new ArgumentException("Filter value must be a non-empty UUID.");

    private static string Method(string value) => value.Trim().ToLowerInvariant() switch
    {
        "card" => "card",
        "promptpay" => "promptpay",
        "installment" => "installment",
        _ => throw new ArgumentException("Invalid payment method."),
    };

    private static DateTime Instant(JsonElement? value) =>
        DateTime.TryParse(String(value), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : throw new ArgumentException("Filter value must be an ISO-8601 instant.");

    private static ReportingBreakdown Breakdown(RawBreakdown value, string key, string label) =>
        new(key, label, value.Currency, value.Amount, value.Count);

    private static AdminDashboardResult EmptyDashboard(ReportingPeriod period) =>
        new(period, [], 0, 0, 0, 0, [], [], []);

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(
        IQueryable<T> query,
        CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => query.ToListAsync(ct), cancellationToken);

    private sealed record StatusCounts(
        long TransactionCount,
        long SuccessCount,
        long FailureCount,
        long PendingCount);

    private sealed record RawBreakdown(
        string Key,
        string Label,
        string Currency,
        decimal Amount,
        long Count);

    private sealed record PspBreakdown(
        Code Psp,
        string Currency,
        decimal Amount,
        long Count);

    private sealed record OriginatorBreakdown(
        Guid? OriginatorId,
        string? Label,
        string Currency,
        decimal Amount,
        long Count);

    private sealed record ItemCountRow(Guid OrderId, Guid MerchantId, int Count);

    private sealed record OutboxProjection(string Type, string Payload);

    private sealed record TransactionRow
    {
        public required Guid TransactionId { get; init; }
        public required Guid OrderId { get; init; }
        public required string OrderNo { get; init; }
        public required Guid MerchantId { get; init; }
        public Guid? OriginatorId { get; init; }
        public string? OriginatorCode { get; init; }
        public string? OriginatorName { get; init; }
        public required decimal Amount { get; init; }
        public required string Currency { get; init; }
        public required string Method { get; init; }
        public required Code Psp { get; init; }
        public required SessionStatus SessionStatus { get; init; }
        public required OrderStatus OrderStatus { get; init; }
        public required string CustomerName { get; init; }
        public required string CustomerPhone { get; init; }
        public string? CustomerEmail { get; init; }
        public string? ExternalChargeId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required DateTime UpdatedAt { get; init; }
        public required long Version { get; init; }
    }
}
