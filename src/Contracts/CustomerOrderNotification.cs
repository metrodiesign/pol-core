using Mediator;

namespace Contracts;

/// <summary>
/// CustomerOrderNotification v1 — emitted (via the transactional outbox) when an order is created with a
/// notification recipient, so the customer is told their summary link out-of-band. Published at-least-once
/// and handled in the background worker; the handler sends it through an <c>INotificationSender</c>. Carries
/// the opaque summary token (the link capability), never a secret.
/// </summary>
public sealed record CustomerOrderNotification(
    Guid TenantId,
    Guid OrderId,
    string Recipient,
    string SummaryToken,
    DateTime OccurredAt) : INotification
{
    public const string SchemaVersion = "v1";
}
