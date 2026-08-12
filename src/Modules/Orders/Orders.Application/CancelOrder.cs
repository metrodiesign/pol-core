using BuildingBlocks.Application;
using Mediator;

namespace Orders.Application;

/// <summary>Cancels an order the customer never paid for (REQ-4). Merchant-scoped; the query filter confines
/// the lookup to the bound merchant, so another company's order reads as absent (404). Releasing whatever
/// payment session is holding the order happens BEFORE this, at the endpoint — an order is only cancellable
/// once no money can still arrive for it.</summary>
public sealed record CancelOrderCommand(Guid OrderId, long? ExpectedVersion = null)
    : ICommand<CancelOrderResult>, IMerchantScoped;

public sealed record CancelOrderResult(Guid OrderId, string Status);

public sealed class CancelOrderHandler : ICommandHandler<CancelOrderCommand, CancelOrderResult>
{
    private readonly IOrderRepository _orders;
    private readonly IPaymentSessionProbe _sessions;
    private readonly IUnitOfWork _unitOfWork;

    public CancelOrderHandler(IOrderRepository orders, IPaymentSessionProbe sessions, IUnitOfWork unitOfWork)
    {
        _orders = orders;
        _sessions = sessions;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<CancelOrderResult> Handle(CancelOrderCommand command, CancellationToken cancellationToken)
    {
        return await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var order = await _orders.GetForUpdateAsync(command.OrderId, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException($"Order {command.OrderId} was not found.");

                if (order.Status == Domain.OrderStatus.Cancelled && command.ExpectedVersion is not null)
                    return new CancelOrderResult(order.Id, order.Status.ToString());
                if (command.ExpectedVersion is { } expected && order.Version != expected)
                    throw new ConcurrencyConflictException("Order changed after it was read.");
                if (order.Status != Domain.OrderStatus.Pending)
                    throw new ConflictException(
                        $"Order {command.OrderId} cannot be cancelled from status {order.Status}.");

                if (await _sessions.HasBlockingSessionAsync(order.Id, ct).ConfigureAwait(false))
                    throw new ConflictException(
                        $"Order {command.OrderId} has an active payment session and cannot be cancelled.");

                order.Cancel(DateTime.UtcNow);
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                return new CancelOrderResult(order.Id, order.Status.ToString());
            },
            cancellationToken).ConfigureAwait(false);
    }
}
