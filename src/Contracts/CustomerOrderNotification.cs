using Mediator;

namespace Contracts;

/// <summary>
/// CustomerOrderNotification v1 — emitted (via the transactional outbox) when an order is created with a
/// notification recipient, so the customer is told their summary link out-of-band. Published at-least-once
/// and handled in the background worker; the handler sends it through an <c>INotificationSender</c>. Carries
/// the opaque summary token (the link capability), never a secret. <see cref="OrderNo"/> is additive
/// (purchase-flow-completion REQ-7.1/7.5) — the human-readable order number to quote in the message;
/// nullable with a default so a v1 payload still in the outbox at deploy time deserialises unchanged.
/// </summary>
public sealed record CustomerOrderNotification(
    Guid MerchantId,
    Guid OrderId,
    string Recipient,
    string SummaryToken,
    DateTime OccurredAt,
    string? OrderNo = null) : INotification
{
    public const string SchemaVersion = "v1";
}
