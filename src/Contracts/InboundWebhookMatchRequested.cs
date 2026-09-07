using Mediator;

namespace Contracts;

/// <summary>
/// A fetch-confirm-only (Omise) webhook arrived before its charge was bound to a session, so it was parked
/// as a pending match (merchant-psp-settings AC-8.4). This event asks the rematcher to try to bind and
/// confirm it. It carries only bounded references — never a raw payload or secret (AC-8.6). It is one of the
/// TWO directions that close the webhook/charge-bind race: this one fires when the webhook lands first,
/// <see cref="PspChargeBound"/> fires when the bind lands first, and both drive the same idempotent rematch.
/// </summary>
public sealed record InboundWebhookMatchRequested(
    Guid EventId,
    Guid MerchantId,
    Guid PspConnectionId,
    string ExternalChargeId,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "payments.inbound-webhook-match-requested.v1";
    public const string SchemaVersion = "v1";
}
