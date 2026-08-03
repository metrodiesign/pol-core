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
        var order = await _orders.GetAsync(command.OrderId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Order {command.OrderId} was not found.");

        // The release at the endpoint proved no session was holding the order THEN; a create-session can
        // still mint one before our own commit. So the flip and the re-check share one transaction: the
        // UPDATE takes the order row's write lock first (a concurrent mint holds that same row locked until
        // its commit — see IPayableOrderReader.GetForMintAsync), and only then do we look for a session that
        // slipped in. Found -> the whole cancel rolls back with 409 (REQ-4.7); the merchant retries and the
        // release deals with the newcomer.
        return await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                // Paid -> InvalidOperationException -> 409 (REQ-4.4); already Cancelled -> no-op (REQ-4.5).
                order.Cancel();
                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                if (await _sessions.HasBlockingSessionAsync(order.Id, ct).ConfigureAwait(false))
                    throw new ConflictException(
                        $"Order {command.OrderId} gained a payment session while being cancelled; the cancel was rolled back.");

                return new CancelOrderResult(order.Id, order.Status.ToString());
            },
            cancellationToken).ConfigureAwait(false);
    }
}
