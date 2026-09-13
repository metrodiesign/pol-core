using BuildingBlocks.Application;
using Mediator;
using Orders.Domain;

namespace Orders.Application;

/// <summary>Reconciliation report for the bound merchant: orders grouped by status + currency with a count
/// and total. Merchant-scoped (RLS); totals are per currency — never summed across currencies (REQ-4).</summary>
public sealed record GetReconciliationSummaryQuery(Guid MerchantId) : IQuery<ReconciliationView>, IMerchantScoped;

/// <summary>Repo projection of one (status, currency) group. Status stays an enum for the SQL GROUP BY.</summary>
public sealed record OrderStatusTotal(OrderStatus Status, string Currency, int Count, decimal Total);

public sealed record ReconciliationLine(string Status, string Currency, int Count, decimal Total);

public sealed record ReconciliationView(IReadOnlyList<ReconciliationLine> Lines);

public sealed class GetReconciliationSummaryHandler : IQueryHandler<GetReconciliationSummaryQuery, ReconciliationView>
{
    private readonly IOrderRepository _orders;

    public GetReconciliationSummaryHandler(IOrderRepository orders) => _orders = orders;

    public async ValueTask<ReconciliationView> Handle(GetReconciliationSummaryQuery query, CancellationToken cancellationToken)
    {
        var totals = await _orders.GetReconciliationAsync(query.MerchantId, cancellationToken).ConfigureAwait(false);

        var lines = totals
            .Select(t => new ReconciliationLine(t.Status.ToString(), t.Currency, t.Count, t.Total))
            .ToList();

        return new ReconciliationView(lines);
    }
}
