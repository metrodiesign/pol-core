using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Application;

/// <summary>
/// Opens the order for a confirmed checkout (REQ-5.2). Idempotent under at-least-once delivery: if an order
/// already exists for the checkout session it skips (REQ-5.3) — and the filtered UNIQUE index on
/// CheckoutSessionId is the hard backstop, so a lost race throws, the message retries, and the retry finds
/// the row and skips. The order number is minted AFTER that skip check, so a replay never burns a second
/// number for an order that already has one (purchase-flow-completion REQ-7.1; a number burnt by a retry
/// that then fails to commit is an accepted gap). Enqueues the customer notification in the same unit of
/// work when a recipient was carried (REQ-5.4), mirroring CreateOrderHandler.
/// </summary>
public sealed class CheckoutConfirmedConsumer : INotificationHandler<CheckoutConfirmed>
{
    private readonly IOrderRepository _orders;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOrderNoSequence _orderNumbers;

    public CheckoutConfirmedConsumer(
        IOrderRepository orders, IOutbox outbox, IUnitOfWork unitOfWork, IClock clock, IOrderNoSequence orderNumbers)
    {
        _orders = orders;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _orderNumbers = orderNumbers;
    }

    public async ValueTask Handle(CheckoutConfirmed notification, CancellationToken cancellationToken)
    {
        var existing = await _orders.GetByCheckoutSessionIdAsync(notification.CheckoutSessionId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
            return; // idempotent skip

        var items = notification.Items
            .Select(i => new OrderItemInput(
                i.ProductId, i.Quantity, i.UnitPrice,
                i.DocumentNo, i.ProductGroup, i.DocumentType, i.PolicyNumber, i.StartDate, i.EndDate,
                i.InsuredFirstName, i.InsuredLastName, i.InsuredIdNumber, i.InsuredDateOfBirth,
                DiscountOf(i)))
            .ToList();

        var orderNo = await _orderNumbers.NextAsync(cancellationToken).ConfigureAwait(false);
        var order = Order.Create(
            notification.MerchantId, notification.Amount, _clock.UtcNow, items, orderNo,
            checkoutSessionId: notification.CheckoutSessionId, notificationRecipient: notification.Recipient,
            paymentChannel: notification.PaymentChannel, customer: CustomerOf(notification));
        _orders.Add(order);

        // The order, not the payload, decides where the link goes — a pre-REQ-6.6 payload has only
        // Recipient, a new one has the contact fields, and Order.Create has already resolved the two (F-03).
        if (!string.IsNullOrWhiteSpace(order.NotificationRecipient))
            _outbox.Enqueue(new CustomerOrderNotification(
                order.MerchantId, order.Id, order.NotificationRecipient, order.SummaryToken, _clock.UtcNow,
                order.OrderNo));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A v1 payload has no discount: its defaults are 0/THB, and that "THB" would clash with a
    /// non-THB line. A zero discount has no currency of its own, so it takes the line's (REQ-7.5).</summary>
    private static Money DiscountOf(CheckoutConfirmedItem item) =>
        item.DiscountAmount == 0m
            ? Money.Zero(item.UnitPrice.Currency)
            : Money.Of(item.DiscountAmount, item.DiscountCurrency);

    /// <summary>A v1 payload carries no contact fields at all — those orders keep the placeholder the
    /// column DEFAULTs hold, and fall back to <c>Recipient</c> for the notification (REQ-7.5).</summary>
    private static CustomerContact? CustomerOf(CheckoutConfirmed notification) =>
        notification.CustomerName is null && notification.CustomerPhone is null && notification.CustomerEmail is null
            ? null
            : CustomerContact.FromStorage(
                notification.CustomerName ?? CustomerContact.UnknownName,
                notification.CustomerPhone ?? string.Empty,
                notification.CustomerEmail);
}
