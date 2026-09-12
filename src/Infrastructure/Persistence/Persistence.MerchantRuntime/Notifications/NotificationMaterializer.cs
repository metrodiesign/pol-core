using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Notifications.Application;
using Notifications.Domain;

namespace Persistence.MerchantRuntime.Notifications;

internal sealed class NotificationMaterializer(
    CommerceDbContext db,
    IUnitOfWork unitOfWork,
    IClock clock,
    IBusinessWebhookConfigurationReader? webhookConfigurations = null) : INotificationMaterializer
{
    public async Task MaterializeAsync(NotificationEvent source, CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var inbox = await PlatformReadGuard.ReadAsync(readCt => db.NotificationInboxMessages
                .SingleOrDefaultAsync(x => x.SourceEventId == source.SourceEventId, readCt), ct);
            if (inbox is not null)
                return false;

            var now = clock.UtcNow;
            var order = source.OrderId is { } orderId
                ? await PlatformReadGuard.ReadAsync(readCt => db.Orders.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == orderId, readCt), ct)
                : null;
            var transactionNo = source.TransactionNo;
            if (transactionNo is null && source.TransactionId is { } transactionId)
            {
                transactionNo = await PlatformReadGuard.ReadAsync(readCt => db.Transactions.AsNoTracking()
                    .Where(x => x.Id == transactionId)
                    .Select(x => x.TransactionNo)
                    .SingleOrDefaultAsync(readCt), ct);
            }
            var notification = global::Notifications.Domain.Notification.Create(
                source.SourceEventId,
                source.MerchantId,
                source.EventType,
                source.PayloadSnapshot,
                source.OccurredAt,
                source.CorrelationId ?? CorrelationId.Current,
                source.RegistrationId,
                source.RegistrationAttemptId,
                source.OrderId,
                source.OrderNo ?? order?.OrderNo,
                source.TransactionId,
                transactionNo);
            db.Notifications.Add(notification);
            db.NotificationInboxMessages.Add(NotificationInboxMessage.Receive(
                source.SourceEventId, source.MerchantId, source.EventType, source.PayloadSnapshot, now));

            var recipients = Recipients(source, order);
            foreach (var (channel, recipient) in recipients)
            {
                await AddDeliveryAsync(notification, source, channel, recipient, now, ct);
            }

            if (webhookConfigurations is not null)
            {
                var endpoint = await webhookConfigurations.GetAsync(
                    source.MerchantId, source.EventType, ct);
                if (endpoint is not null)
                    await AddDeliveryAsync(
                        notification,
                        source,
                        "business_webhook",
                        endpoint.Url,
                        now,
                        ct,
                        endpoint.Url,
                        endpoint.ProtectedSecret);
            }

            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
    }

    private async Task AddDeliveryAsync(
        global::Notifications.Domain.Notification notification,
        NotificationEvent source,
        string channel,
        string recipient,
        DateTime now,
        CancellationToken cancellationToken,
        string? endpointUrl = null,
        string? protectedEndpointSecret = null)
    {
        var definition = NotificationTemplates.For(source.EventType, channel);
        var template = await PlatformReadGuard.ReadAsync(readCt => db.TemplateVersions
            .SingleOrDefaultAsync(x => x.EventType == definition.EventType
                && x.Channel == definition.Channel
                && x.Version == definition.Version
                && x.Locale == definition.Locale, readCt), cancellationToken);
        if (template is null)
        {
            template = TemplateVersion.Release(
                definition.EventType,
                definition.Channel,
                definition.Version,
                definition.Locale,
                definition.Subject,
                definition.Content,
                now);
            db.TemplateVersions.Add(template);
        }

        db.Deliveries.Add(Delivery.Create(
            notification.Id,
            source.SourceEventId,
            source.MerchantId,
            channel,
            recipient,
            template,
            source.PayloadSnapshot,
            now,
            endpointUrl,
            protectedEndpointSecret));
    }

    private static IReadOnlyList<(string Channel, string Recipient)> Recipients(
        NotificationEvent source, global::Orders.Domain.Order? order)
    {
        var recipients = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(source.Email))
            recipients.Add(("email", source.Email.Trim()));
        if (!string.IsNullOrWhiteSpace(source.PhoneNumber))
            recipients.Add(("sms", source.PhoneNumber.Trim()));

        if (order is null)
            return recipients;
        if (!recipients.Any(x => x.Item1 == "email") && !string.IsNullOrWhiteSpace(order.CustomerEmail))
            recipients.Add(("email", order.CustomerEmail));
        if (!recipients.Any(x => x.Item1 == "sms") && !string.IsNullOrWhiteSpace(order.CustomerPhone))
            recipients.Add(("sms", order.CustomerPhone));
        return recipients;
    }
}
