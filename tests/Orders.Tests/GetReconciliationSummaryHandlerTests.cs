using Orders.Application;
using Orders.Domain;

namespace Orders.Tests;

public sealed class GetReconciliationSummaryHandlerTests
{
    [Fact]
    public async Task It_maps_repo_totals_to_view_lines_with_status_names()
    {
        var repo = new FakeOrderRepository
        {
            Reconciliation =
            [
                new OrderStatusTotal(OrderStatus.Paid, "THB", 3, 45000),
                new OrderStatusTotal(OrderStatus.AwaitingPayment, "THB", 2, 30000),
                new OrderStatusTotal(OrderStatus.Paid, "USD", 1, 999),
            ],
        };
        var handler = new GetReconciliationSummaryHandler(repo);

        var view = await handler.Handle(new GetReconciliationSummaryQuery(Guid.NewGuid()), default);

        Assert.Equal(3, view.Lines.Count);
        var paidThb = view.Lines.Single(l => l.Status == "Paid" && l.Currency == "THB");
        Assert.Equal(3, paidThb.Count);
        Assert.Equal(45000, paidThb.TotalMinorUnits);
        // USD kept as its own line — never summed across currencies.
        Assert.Contains(view.Lines, l => l.Currency == "USD" && l.TotalMinorUnits == 999);
    }
}
