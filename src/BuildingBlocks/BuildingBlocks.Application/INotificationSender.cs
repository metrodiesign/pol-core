namespace BuildingBlocks.Application;

/// <summary>What the customer notification carries to the delivery channel. The summary token is the link
/// capability; the concrete sender builds the full URL. No secret/PII beyond the recipient handle.</summary>
public sealed record NotificationMessage(Guid MerchantId, Guid OrderId, string Recipient, string SummaryToken);

/// <summary>
/// Sends a customer notification (the order summary link) over the chosen channel (email/SMS). A port so
/// the delivery provider is swappable: the default implementation logs, and a real provider is wired via DI
/// without touching the modules. Invoked by the outbox-driven background worker, so a throw is retried.
/// </summary>
public interface INotificationSender
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}
