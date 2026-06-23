using Mediator;

namespace Contracts;

/// <summary>
/// CheckoutConfirmed v1 — emitted (via the transactional outbox) when a checkout is confirmed, so the
/// Orders module can open the order out-of-band. Published at-least-once; the consumer is idempotent on
/// <see cref="CheckoutSessionId"/> (one order per checkout). Carries the agreed amount + the optional
/// notification recipient so the created order can notify the customer.
/// </summary>
public sealed record CheckoutConfirmed(
    Guid TenantId,
    Guid CheckoutSessionId,
    long AmountMinorUnits,
    string Currency,
    string? Recipient,
    DateTime OccurredAt) : INotification
{
    public const string SchemaVersion = "v1";
}
