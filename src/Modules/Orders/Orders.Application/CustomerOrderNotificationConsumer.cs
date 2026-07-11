using BuildingBlocks.Application;
using Contracts;
using Mediator;

namespace Orders.Application;

/// <summary>
/// Background consumer of <see cref="CustomerOrderNotification"/> (published via the outbox when an order
/// is created with a recipient). Sends the summary link through the <see cref="INotificationSender"/> port.
/// A throw propagates to the outbox dispatcher, which retries with backoff and eventually parks the message
/// for DLQ review (REQ-3.4) — delivery is never lost and never blocks order creation.
/// </summary>
public sealed class CustomerOrderNotificationConsumer : INotificationHandler<CustomerOrderNotification>
{
    private readonly INotificationSender _sender;

    public CustomerOrderNotificationConsumer(INotificationSender sender) => _sender = sender;

    public ValueTask Handle(CustomerOrderNotification notification, CancellationToken cancellationToken) =>
        new(_sender.SendAsync(
            new NotificationMessage(notification.MerchantId, notification.OrderId, notification.Recipient, notification.SummaryToken),
            cancellationToken));
}
