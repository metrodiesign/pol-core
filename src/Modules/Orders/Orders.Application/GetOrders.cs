using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Orders.Application;

/// <summary>Merchant-authenticated order list — the masked read surface REQ-7.4 names alongside the
/// detail read. Merchant-scoped via <see cref="IMerchantScoped"/> + the query filter floor.
/// <paramref name="OrderNo"/> is the one filter this surface takes (purchase-flow-completion REQ-7.4),
/// parsed at the host from the SFS <c>filters</c> contract; null = no filter.</summary>
public sealed record GetOrdersQuery(Guid MerchantId, string? OrderNo = null) : IQuery<OrdersListView>, IMerchantScoped;

/// <summary>Generic list line. Metadata is intentionally absent.</summary>
public sealed record OrderItemListItem(
    string ProductCode, string VariantCode, string? VariantName,
    int Quantity, Money UnitPrice, Money Discount);

public sealed record OrderListItem(
    Guid OrderId, string OrderNo, string Status, Money Amount, DateTime CreatedAt, string? PaymentChannel,
    IReadOnlyList<OrderItemListItem> Lines);

public sealed record OrdersListView(IReadOnlyList<OrderListItem> Orders);

public sealed class GetOrdersHandler : IQueryHandler<GetOrdersQuery, OrdersListView>
{
    private readonly IOrderRepository _orders;

    public GetOrdersHandler(IOrderRepository orders) => _orders = orders;

    public async ValueTask<OrdersListView> Handle(GetOrdersQuery query, CancellationToken cancellationToken)
    {
        var orders = await _orders.ListAsync(query.MerchantId, query.OrderNo, cancellationToken).ConfigureAwait(false);

        var items = orders.Select(o => new OrderListItem(
            o.Id, o.OrderNo, o.Status.ToString(), o.Amount, o.CreatedAt, o.PaymentChannel,
            o.Items.Select(i => new OrderItemListItem(
                i.ProductCode, i.VariantCode, i.VariantName, i.Quantity, i.UnitPrice, i.Discount))
                .ToList()))
            .ToList();

        return new OrdersListView(items);
    }
}
