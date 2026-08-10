using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Orders.Application;

/// <summary>Merchant-authenticated order list — the masked read surface REQ-7.4 names alongside the
/// detail read. Merchant-scoped via <see cref="IMerchantScoped"/> + the query filter floor.
/// Paging, filtering and sorting use the shared SFS contract with a deny-by-default repository whitelist.</summary>
public sealed record GetOrdersQuery(Guid MerchantId)
    : PagedQuery, IQuery<PagedResult<OrderListItem>>, IMerchantScoped;

/// <summary>Generic list line. Metadata is intentionally absent.</summary>
public sealed record OrderItemListItem(
    string ProductCode, string VariantCode, string? VariantName,
    int Quantity, Money UnitPrice, Money Discount);

public sealed record OrderListItem(
    Guid OrderId, string OrderNo, string Status, Money Amount, DateTime CreatedAt, string? PaymentChannel,
    string CustomerName, string CustomerPhone, string? CustomerEmail,
    IReadOnlyList<OrderItemListItem> Lines);

public sealed class GetOrdersHandler : IQueryHandler<GetOrdersQuery, PagedResult<OrderListItem>>
{
    private readonly IOrderRepository _orders;

    public GetOrdersHandler(IOrderRepository orders) => _orders = orders;

    public async ValueTask<PagedResult<OrderListItem>> Handle(
        GetOrdersQuery query,
        CancellationToken cancellationToken)
    {
        var orders = await _orders.ListAsync(query.MerchantId, query, cancellationToken).ConfigureAwait(false);

        var items = orders.Items.Select(o => new OrderListItem(
            o.Id, o.OrderNo, o.Status.ToString(), o.Amount, o.CreatedAt, o.PaymentChannel,
            o.CustomerName, o.CustomerPhone, o.CustomerEmail,
            o.Items.Select(i => new OrderItemListItem(
                i.ProductCode, i.VariantCode, i.VariantName, i.Quantity, i.UnitPrice, i.Discount))
                .ToList()))
            .ToList();

        return new PagedResult<OrderListItem>(items, orders.Page, orders.Limit, orders.Total);
    }
}
