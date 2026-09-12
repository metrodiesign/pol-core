using Mediator;

namespace Contracts;

/// <summary>
/// A hosted PSP charge was just bound to a payment session (merchant-psp-settings AC-8.4). For a
/// fetch-confirm-only provider (Omise) this is the second of the two directions that close the
/// webhook/charge-bind race: if a webhook was parked as a pending match before the bind, this event drives
/// the same idempotent rematch that <see cref="InboundWebhookMatchRequested"/> would. Carries only bounded
/// references — never a raw payload or secret (AC-8.6). Emitted only for fetch-confirm-only providers, so
/// signed-deterministic providers (2C2P) do not write an outbox row with no consumer.
/// </summary>
public sealed record PspChargeBound(
    Guid EventId,
    Guid MerchantId,
    Guid PspConnectionId,
    Guid PaymentSessionId,
    string ExternalChargeId,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "payments.psp-charge-bound.v1";
    public const string SchemaVersion = "v1";
}
