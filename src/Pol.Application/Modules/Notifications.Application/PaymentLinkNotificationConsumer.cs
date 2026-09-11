using System.Text.Json;
using Contracts;
using Mediator;

namespace Notifications.Application;

/// <summary>Moves the protected payment-link event into the Commerce notification projection. The protected
/// token remains ciphertext in Notification/Delivery payload snapshots; delivery-time rendering owns the
/// only unprotect operation.</summary>
public sealed class PaymentLinkNotificationRequestedV1Handler(INotificationMaterializer materializer)
    : INotificationHandler<PaymentLinkNotificationRequestedV1>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask Handle(
        PaymentLinkNotificationRequestedV1 notification,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.ProtectedRawToken))
            throw new InvalidOperationException("Payment-link notification protection is missing.");
        await materializer.MaterializeAsync(new NotificationEvent(
            notification.EventId,
            notification.MerchantId,
            PaymentLinkNotificationRequestedV1.EventType,
            JsonSerializer.Serialize(notification, JsonOptions),
            notification.OccurredAt,
            notification.Email,
            notification.PhoneNumber,
            OrderId: notification.OrderId,
            CorrelationId: notification.LinkId.ToString("D")), cancellationToken);
    }
}
