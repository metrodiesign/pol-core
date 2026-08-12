using System.Text.Json;
using Contracts;
using Mediator;

namespace Notifications.Application;

public sealed class PaymentPaidDeliveryConsumer(IDeliveryEventSink sink)
    : INotificationHandler<PaymentPaid>
{
    public async ValueTask Handle(PaymentPaid notification, CancellationToken ct) =>
        await sink.EnqueueAsync(notification.EventId, notification.MerchantId, "payment.paid",
            notification.PaymentSessionId.ToString("D"), JsonSerializer.Serialize(new
            {
                id = notification.EventId,
                type = "payment.paid",
                occurredAt = notification.OccurredAt,
                data = new
                {
                    paymentSessionId = notification.PaymentSessionId,
                    orderId = notification.OrderId,
                    amount = new { notification.Amount.Amount, currency = notification.Amount.Currency },
                    notification.Method,
                    notification.PspCode,
                },
            }), ct);
}

public sealed class PaymentFailedDeliveryConsumer(IDeliveryEventSink sink)
    : INotificationHandler<PaymentFailed>
{
    public async ValueTask Handle(PaymentFailed notification, CancellationToken ct) =>
        await sink.EnqueueAsync(notification.EventId, notification.MerchantId, "payment.failed",
            notification.PaymentSessionId.ToString("D"), JsonSerializer.Serialize(new
            {
                id = notification.EventId,
                type = "payment.failed",
                occurredAt = notification.OccurredAt,
                data = new
                {
                    paymentSessionId = notification.PaymentSessionId,
                    orderId = notification.OrderId,
                    notification.ReasonCode,
                },
            }), ct);
}

public sealed class PaymentExpiredDeliveryConsumer(IDeliveryEventSink sink)
    : INotificationHandler<PaymentExpired>
{
    public async ValueTask Handle(PaymentExpired notification, CancellationToken ct) =>
        await sink.EnqueueAsync(notification.EventId, notification.MerchantId, "payment.expired",
            notification.PaymentSessionId.ToString("D"), JsonSerializer.Serialize(new
            {
                id = notification.EventId,
                type = "payment.expired",
                occurredAt = notification.OccurredAt,
                data = new
                {
                    paymentSessionId = notification.PaymentSessionId,
                    orderId = notification.OrderId,
                },
            }), ct);
}
