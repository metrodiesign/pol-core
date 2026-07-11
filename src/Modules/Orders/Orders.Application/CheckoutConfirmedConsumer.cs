using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Orders.Domain;

namespace Orders.Application;

/// <summary>
/// Opens the order for a confirmed checkout (REQ-5.2). Idempotent under at-least-once delivery: if an order
/// already exists for the checkout session it skips (REQ-5.3) — and the filtered UNIQUE index on
/// CheckoutSessionId is the hard backstop, so a lost race throws, the message retries, and the retry finds
/// the row and skips. Enqueues the customer notification in the same unit of work when a recipient was
/// carried (REQ-5.4), mirroring CreateOrderHandler.
/// </summary>
public sealed class CheckoutConfirmedConsumer : INotificationHandler<CheckoutConfirmed>
{
    private readonly IOrderRepository _orders;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CheckoutConfirmedConsumer(IOrderRepository orders, IOutbox outbox, IUnitOfWork unitOfWork, IClock clock)
    {
        _orders = orders;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask Handle(CheckoutConfirmed notification, CancellationToken cancellationToken)
    {
        var existing = await _orders.GetByCheckoutSessionIdAsync(notification.CheckoutSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return; // idempotent skip

        var order = Order.Create(
            notification.MerchantId, notification.Amount, _clock.UtcNow,
            checkoutSessionId: notification.CheckoutSessionId, notificationRecipient: notification.Recipient);
        _orders.Add(order);

        if (!string.IsNullOrWhiteSpace(notification.Recipient))
            _outbox.Enqueue(new CustomerOrderNotification(
                order.MerchantId, order.Id, notification.Recipient, order.SummaryToken, _clock.UtcNow));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
