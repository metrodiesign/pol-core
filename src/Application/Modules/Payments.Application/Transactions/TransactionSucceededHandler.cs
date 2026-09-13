using Platform.Application.Transactions;
using Contracts;
using Mediator;
using Notifications.Application;

namespace Payments.Application.Transactions;

/// <summary>Keeps the outbox contract dispatchable until Notification owns its delivery projection in Task 7.</summary>
public sealed class TransactionSucceededHandler(INotificationMaterializer materializer)
    : INotificationHandler<TransactionSucceeded>
{
    public async ValueTask Handle(TransactionSucceeded notification, CancellationToken cancellationToken) =>
        await materializer.MaterializeAsync(new NotificationEvent(
            notification.EventId,
            notification.MerchantId,
            TransactionSucceeded.EventType,
            System.Text.Json.JsonSerializer.Serialize(notification),
            notification.OccurredAt,
            OrderId: notification.OrderId,
            TransactionId: notification.TransactionId), cancellationToken);
}
