using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// Reads an order summary by its link token from <c>shop.Orders</c> directly — rls-to-query-filter
/// task 8 dropped <c>sec.usp_resolve_order_summary</c> along with the rest of the RLS apparatus (mirrors
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

        var rows = await db.Database
            .SqlQueryRaw<OrderSummaryRow>(
                "SELECT TOP 1 Id, MerchantId, AmountAmount, AmountCurrency, Status, PaymentSessionId, SummaryTokenExpiresAt "
                + "FROM shop.Orders WHERE SummaryToken = {0}",
                token)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return null;

        var r = rows[0];
        return new OrderSummary(
            r.Id, Money.Of(r.AmountAmount, r.AmountCurrency), ((OrderStatus)r.Status).ToString(), r.PaymentSessionId, r.SummaryTokenExpiresAt);
    }
}

/// <summary>Unmapped projection for the resolver query's result set (matched to its SELECT by column name).</summary>
internal sealed class OrderSummaryRow
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public decimal AmountAmount { get; set; }
    public string AmountCurrency { get; set; } = default!;
    public int Status { get; set; }
    public Guid? PaymentSessionId { get; set; }
    public DateTime SummaryTokenExpiresAt { get; set; }
}
