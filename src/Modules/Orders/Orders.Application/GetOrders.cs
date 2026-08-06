using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Orders.Application;

/// <summary>Merchant-authenticated order list — the masked read surface REQ-7.4 names alongside the
/// detail read. Merchant-scoped via <see cref="IMerchantScoped"/> + the query filter floor.
/// <paramref name="OrderNo"/> is the one filter this surface takes (purchase-flow-completion REQ-7.4),
/// parsed at the host from the SFS <c>filters</c> contract; null = no filter.</summary>
public sealed record GetOrdersQuery(Guid MerchantId, string? OrderNo = null) : IQuery<OrdersListView>, IMerchantScoped;

/// <summary>One item on the list surface — <see cref="InsuredFirstName"/>/<see cref="InsuredLastName"/>/
/// <see cref="InsuredDateOfBirth"/> as-is, <see cref="MaskedInsuredIdNumber"/> always masked (REQ-7.4).
/// No reveal audit on this surface — nothing full-value is disclosed here.</summary>
public sealed record OrderItemListItem(
    Money UnitPrice, Money Discount,
    string DocumentNo, string ProductGroup, string DocumentType, string? PolicyNumber,
    DateTime? StartDate, DateTime? EndDate,
    string InsuredFirstName, string InsuredLastName, string MaskedInsuredIdNumber, DateTime InsuredDateOfBirth);

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
                i.UnitPrice, i.Discount,
                i.DocumentNo, i.ProductGroup, i.DocumentType, i.PolicyNumber, i.StartDate, i.EndDate,
                i.InsuredFirstName, i.InsuredLastName, MaskIdNumber(i.InsuredIdNumber), i.InsuredDateOfBirth))
                .ToList()))
            .ToList();

        return new OrdersListView(items);
    }

    // Local to this read model's projection, deliberately not shared with Payments' PspSecretEnvelopeFactory
    // (design.md non-goal) or reused as a cross-file utility — see OrderSummaryReader's own copy.
    private static string MaskIdNumber(string idNumber) =>
        idNumber.Length <= 4 ? new string('*', idNumber.Length) : $"****{idNumber[^4..]}";
}
