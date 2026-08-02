using BuildingBlocks.Application;
using Mediator;

namespace Orders.Application;

/// <summary>Cancels an order the customer never paid for (REQ-4). Merchant-scoped; the query filter confines
/// the lookup to the bound merchant, so another company's order reads as absent (404). Releasing whatever
/// payment session is holding the order happens BEFORE this, at the endpoint — an order is only cancellable
/// once no money can still arrive for it.</summary>
public sealed record CancelOrderCommand(Guid OrderId) : ICommand<CancelOrderResult>, IMerchantScoped;

public sealed record CancelOrderResult(Guid OrderId, string Status);

public sealed class CancelOrderHandler : ICommandHandler<CancelOrderCommand, CancelOrderResult>
{
    private readonly IOrderRepository _orders;
    private readonly IUnitOfWork _unitOfWork;

    public CancelOrderHandler(IOrderRepository orders, IUnitOfWork unitOfWork)
    {
        _orders = orders;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<CancelOrderResult> Handle(CancelOrderCommand command, CancellationToken cancellationToken)
    {
        var order = await _orders.GetAsync(command.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Order {command.OrderId} was not found.");

        // Paid -> InvalidOperationException -> 409 (REQ-4.4); already Cancelled -> no-op (REQ-4.5).
        order.Cancel();
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CancelOrderResult(order.Id, order.Status.ToString());
    }
}
