using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Orders.Application;

/// <summary>Merchant-authenticated order list — the masked read surface REQ-7.4 names alongside the
/// detail read. Merchant-scoped via <see cref="IMerchantScoped"/> + the query filter floor.</summary>
public sealed record GetOrdersQuery(Guid MerchantId) : IQuery<OrdersListView>, IMerchantScoped;

/// <summary>One item on the list surface — <see cref="InsuredFirstName"/>/<see cref="InsuredLastName"/>/
/// <see cref="InsuredDateOfBirth"/> as-is, <see cref="MaskedInsuredIdNumber"/> always masked (REQ-7.4).
/// No reveal audit on this surface — nothing full-value is disclosed here.</summary>
public sealed record OrderItemListItem(
    Guid ProductId, Money UnitPrice, Money SumInsured, int CoverageDurationDays, string Insurer,
    string InsuredFirstName, string InsuredLastName, string MaskedInsuredIdNumber, DateTime InsuredDateOfBirth);

public sealed record OrderListItem(
    Guid OrderId, string Status, Money Amount, DateTime CreatedAt, IReadOnlyList<OrderItemListItem> Lines);

public sealed record OrdersListView(IReadOnlyList<OrderListItem> Orders);

public sealed class GetOrdersHandler : IQueryHandler<GetOrdersQuery, OrdersListView>
{
    private readonly IOrderRepository _orders;

    public GetOrdersHandler(IOrderRepository orders) => _orders = orders;

    public async ValueTask<OrdersListView> Handle(GetOrdersQuery query, CancellationToken cancellationToken)
    {
        var orders = await _orders.ListAsync(query.MerchantId, cancellationToken).ConfigureAwait(false);

        var items = orders.Select(o => new OrderListItem(
            o.Id, o.Status.ToString(), o.Amount, o.CreatedAt,
            o.Items.Select(i => new OrderItemListItem(
                i.ProductId, i.UnitPrice, i.SumInsured, i.CoverageDurationDays, i.Insurer,
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
