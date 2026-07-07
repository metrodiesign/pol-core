using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orders.Application;
using Orders.Domain;

namespace Orders.Infrastructure;

/// <summary>
/// Reads an order summary by its link token via <c>VCentralPay.usp_resolve_order_summary</c> — a proc that
/// runs WITH EXECUTE AS the bypass resolver, so it reads the one order the token names while the calling
/// principal stays RLS-blocked (mirrors <c>WebhookTenantResolver</c>). Runs in a FRESH DI scope so the
/// anonymous request's own DbContext connection is not opened with an empty SESSION_CONTEXT and reused.
/// </summary>
public sealed class OrderSummaryReader : IOrderSummaryReader
{
    private readonly IServiceScopeFactory _scopeFactory;

    public OrderSummaryReader(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<OrderSummary?> GetByTokenAsync(string token, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProducerDbContext>();

        var rows = await db.Database
            .SqlQueryRaw<OrderSummaryRow>("EXEC VCentralPay.usp_resolve_order_summary @Token = {0}", token)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return null;

        var r = rows[0];
        return new OrderSummary(
            r.Id, r.AmountMinorUnits, r.AmountCurrency, ((OrderStatus)r.Status).ToString(), r.PaymentSessionId, r.SummaryTokenExpiresAt);
    }
}

/// <summary>Unmapped projection for the resolver proc's result set (matched to its SELECT by column name).</summary>
public sealed class OrderSummaryRow
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public long AmountMinorUnits { get; set; }
    public string AmountCurrency { get; set; } = default!;
    public int Status { get; set; }
    public Guid? PaymentSessionId { get; set; }
    public DateTime SummaryTokenExpiresAt { get; set; }
}
