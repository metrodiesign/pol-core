using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// Reads an order summary by its link token from <c>shop.Orders</c> / <c>shop.OrderItems</c> directly —
/// rls-to-query-filter task 8 dropped <c>sec.usp_resolve_order_summary</c> along with the rest of the RLS apparatus (mirrors
/// <c>WebhookMerchantResolver</c>). Runs in a FRESH DI scope so the anonymous request's own DbContext
/// connection is not opened pre-bind and reused.
/// </summary>
internal sealed class OrderSummaryReader : IOrderSummaryReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    public OrderSummaryReader(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<OrderSummary?> GetByTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MerchantRuntimeDbContext>();

        var rows = await PlatformReadGuard.ReadAsync(ct => db.Database
            .SqlQueryRaw<OrderSummaryRow>(
                "SELECT TOP 1 Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, PaymentChannel, "
                + "SummaryTokenExpiresAt FROM shop.Orders WHERE SummaryToken = {0}",
                token)
            .ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return null;

        var r = rows[0];

        var lineRows = await PlatformReadGuard.ReadAsync(ct => db.Database
            .SqlQueryRaw<OrderSummaryLineRow>(
                "SELECT DocumentNo, InsuredFirstName, InsuredLastName, InsuredIdNumber FROM shop.OrderItems WHERE OrderId = {0}",
                r.Id)
            .ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false);

        var lines = lineRows
            .Select(l => new OrderSummaryLine(l.DocumentNo, l.InsuredFirstName, l.InsuredLastName, MaskIdNumber(l.InsuredIdNumber)))
            .ToList();

        return new OrderSummary(
            r.Id, r.MerchantId, r.OrderNo, Money.Of(r.AmountAmount, r.AmountCurrency),
            ((OrderStatus)r.Status).ToString(), r.PaymentChannel, r.SummaryTokenExpiresAt, lines);
    }

    // Local to this read model's projection, deliberately not shared with Payments' PspSecretEnvelopeFactory
    // (design.md non-goal) or reused as a cross-file utility — see GetOrdersHandler's own copy.
    private static string MaskIdNumber(string idNumber) =>
        idNumber.Length <= 4 ? new string('*', idNumber.Length) : $"****{idNumber[^4..]}";
}

/// <summary>Unmapped projection for the resolver query's result set (matched to its SELECT by column name).</summary>
internal sealed class OrderSummaryRow
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public string OrderNo { get; set; } = default!;
    public decimal AmountAmount { get; set; }
    public string AmountCurrency { get; set; } = default!;
    public int Status { get; set; }
    public string? PaymentChannel { get; set; }
    public DateTime SummaryTokenExpiresAt { get; set; }
}

/// <summary>Unmapped projection for the order-lines query — deliberately excludes InsuredDateOfBirth
/// (never fetched for this surface, not just omitted at serialization) and merchant/premium data (out of
/// scope for an anonymous customer link).</summary>
internal sealed class OrderSummaryLineRow
{
    public string DocumentNo { get; set; } = default!;
    public string InsuredFirstName { get; set; } = default!;
    public string InsuredLastName { get; set; } = default!;
    public string InsuredIdNumber { get; set; } = default!;
}
