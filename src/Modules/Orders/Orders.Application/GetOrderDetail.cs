using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Orders.Application;

/// <summary>
/// Merchant-authenticated single-order read — every item's <see cref="OrderItemDetail.InsuredIdNumber"/> is
/// returned in FULL (REQ-7.4's detail-read exception to masking), and writes one <see cref="IRevealAuditWriter"/>
/// row per item returned, BEFORE the response is built (REQ-7.5). Modeled as a command, not a query — like
/// <see cref="ResendOrderSummaryCommand"/>, it has a real write side effect (the audit rows), so it goes
/// through <see cref="IUnitOfWork.SaveChangesAsync"/> like any other write. Fail-closed: if that save throws,
/// the handler throws too and the shared exception handler turns it into a 5xx with no PII in the response —
/// a reveal that cannot be proven audited must not happen.
/// </summary>
public sealed record GetOrderDetailCommand(Guid MerchantId, Guid OrderId, string ActorType, string ActorId)
    : ICommand<OrderDetailView>, IMerchantScoped;

public sealed record OrderItemDetail(
    Guid ProductId, int Quantity, Money UnitPrice, Money SumInsured, int CoverageDurationDays, string Insurer,
    string InsuredFirstName, string InsuredLastName, string InsuredIdNumber, DateTime InsuredDateOfBirth);

public sealed record OrderDetailView(Guid OrderId, string Status, Money Amount, IReadOnlyList<OrderItemDetail> Lines);

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
            i.ProductId, i.Quantity, i.UnitPrice, i.SumInsured, i.CoverageDurationDays, i.Insurer,
            i.InsuredFirstName, i.InsuredLastName, i.InsuredIdNumber, i.InsuredDateOfBirth)).ToList();

        return new OrderDetailView(order.Id, order.Status.ToString(), order.Amount, lines);
    }
}
