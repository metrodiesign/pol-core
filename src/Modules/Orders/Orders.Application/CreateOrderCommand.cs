using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Orders.Domain;
using SharedKernel;

namespace Orders.Application;

/// <summary>
/// Opens a new order awaiting payment for the active merchant. Merchant-scoped: rejected by the merchant
/// guard if no merchant is bound to the request (PLAN decision #4). An optional notification
/// <paramref name="Recipient"/> (the customer's email/phone, set at checkout) drives the summary-link
/// notification; absent means no notification is enqueued.
/// </summary>
public sealed record CreateOrderCommand(
    Guid MerchantId, Money Amount, string? Recipient = null, Guid? CheckoutSessionId = null)
    : ICommand<CreateOrderResult>, IMerchantScoped;

/// <summary>The identity of the newly created order.</summary>
public sealed record CreateOrderResult(Guid OrderId);

/// <summary>Handles <see cref="CreateOrderCommand"/>: builds the aggregate, enqueues the customer
/// notification in the SAME unit of work (transactional outbox), and commits. Scoped.</summary>
public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand, CreateOrderResult>
{
    private readonly IOrderRepository _orders;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateOrderHandler(IOrderRepository orders, IOutbox outbox, IUnitOfWork unitOfWork, IClock clock)
    {
        _orders = orders;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<CreateOrderResult> Handle(CreateOrderCommand command, CancellationToken cancellationToken)
    {
        var order = Order.Create(command.MerchantId, command.Amount, _clock.UtcNow,
            checkoutSessionId: command.CheckoutSessionId, notificationRecipient: command.Recipient);

        _orders.Add(order);

        // Enqueue the customer notification atomically with the order (REQ-3.1) — the background worker
        // delivers it, so creation never blocks on or fails for delivery (REQ-3.2).
        if (!string.IsNullOrWhiteSpace(command.Recipient))
            _outbox.Enqueue(new CustomerOrderNotification(
                order.MerchantId, order.Id, command.Recipient, order.SummaryToken, _clock.UtcNow));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateOrderResult(order.Id);
    }
}
