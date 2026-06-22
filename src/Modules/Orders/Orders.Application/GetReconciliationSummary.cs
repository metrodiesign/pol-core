using BuildingBlocks.Application;
using Mediator;
using Orders.Domain;

namespace Orders.Application;

/// <summary>Reconciliation report for the bound tenant: orders grouped by status + currency with a count
/// and total. Tenant-scoped (RLS); totals are per currency — never summed across currencies (REQ-4).</summary>
public sealed record GetReconciliationSummaryQuery(Guid TenantId) : IQuery<ReconciliationView>, ITenantScoped;

/// <summary>Repo projection of one (status, currency) group. Status stays an enum for the SQL GROUP BY.</summary>
public sealed record OrderStatusTotal(OrderStatus Status, string Currency, int Count, long TotalMinorUnits);

public sealed record ReconciliationLine(string Status, string Currency, int Count, long TotalMinorUnits);

public sealed record ReconciliationView(IReadOnlyList<ReconciliationLine> Lines);

public sealed class GetReconciliationSummaryHandler : IQueryHandler<GetReconciliationSummaryQuery, ReconciliationView>
{
    private readonly IOrderRepository _orders;

    public GetReconciliationSummaryHandler(IOrderRepository orders) => _orders = orders;

    public async ValueTask<ReconciliationView> Handle(GetReconciliationSummaryQuery query, CancellationToken cancellationToken)
    {
        var totals = await _orders.GetReconciliationAsync(query.TenantId, cancellationToken).ConfigureAwait(false);

        var lines = totals
            .Select(t => new ReconciliationLine(t.Status.ToString(), t.Currency, t.Count, t.TotalMinorUnits))
            .ToList();

        return new ReconciliationView(lines);
    }
}
