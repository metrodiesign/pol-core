using BuildingBlocks.Application;
using Mediator;
using SharedKernel;
using System.Text.Json;

namespace Orders.Application;

/// <summary>
/// Merchant-authenticated single-order read. Returning server-owned metadata writes one reveal audit row per
/// item before response build. Modeled as a command, not a query — like
/// <see cref="ResendOrderSummaryCommand"/>, it has a real write side effect (the audit rows), so it goes
/// through <see cref="IUnitOfWork.SaveChangesAsync"/> like any other write. Fail-closed: if that save throws,
/// the handler throws too and the shared exception handler turns it into a 5xx with no PII in the response —
/// a reveal that cannot be proven audited must not happen.
/// </summary>
public sealed record GetOrderDetailCommand(Guid MerchantId, Guid OrderId, string ActorType, string ActorId)
    : ICommand<OrderDetailView>, IMerchantScoped;

public sealed record OrderItemDetail(
    string ProductCode, string VariantCode, string? VariantName,
    int Quantity, Money UnitPrice, Money Discount, JsonElement? Metadata);

/// <summary>The single-order read (purchase-flow-completion REQ-7.3 adds <see cref="OrderNo"/>,
/// <see cref="PaymentChannel"/> and the buyer's contact to what the merchant sees).</summary>
public sealed record OrderDetailView(
    Guid OrderId, string OrderNo, string Status, Money Amount, string? PaymentChannel,
    string CustomerName, string CustomerPhone, string? CustomerEmail,
    IReadOnlyList<OrderItemDetail> Lines);

public sealed class GetOrderDetailHandler : ICommandHandler<GetOrderDetailCommand, OrderDetailView>
{
    private readonly IOrderRepository _orders;
    private readonly IRevealAuditWriter _audits;
    private readonly IUnitOfWork _unitOfWork;

    public GetOrderDetailHandler(IOrderRepository orders, IRevealAuditWriter audits, IUnitOfWork unitOfWork)
    {
        _orders = orders;
        _audits = audits;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<OrderDetailView> Handle(GetOrderDetailCommand command, CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(command.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Order {command.OrderId} was not found.");

        foreach (var item in order.Items)
            await _audits.AppendAsync(
                item.Id, command.MerchantId, command.ActorType, command.ActorId, CorrelationId.Current, cancellationToken)
                .ConfigureAwait(false);

        // Fail-closed: nothing below runs, and no response is built, if the audit rows above fail to save.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var lines = order.Items.Select(i => new OrderItemDetail(
            i.ProductCode, i.VariantCode, i.VariantName, i.Quantity, i.UnitPrice, i.Discount,
            i.Metadata is null ? null : CommerceItemMetadataCodec.ToJsonElement(i.Metadata))).ToList();

        return new OrderDetailView(
            order.Id, order.OrderNo, order.Status.ToString(), order.Amount, order.PaymentChannel,
            order.CustomerName, order.CustomerPhone, order.CustomerEmail, lines);
    }
}
