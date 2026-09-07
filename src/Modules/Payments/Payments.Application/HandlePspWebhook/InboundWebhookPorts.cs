using Payments.Domain;
using SharedKernel;

namespace Payments.Application.HandlePspWebhook;

public sealed record InboundWebhookClaim(
    Guid EventId,
    InboundWebhookStatus Status,
    string PayloadFingerprint);

public interface IInboundWebhookRecorder
{
    Task RecordRejectedAsync(Guid connectionId, Guid merchantId, string pspCode,
        string payloadFingerprint, bool signatureValid, string failureCode, CancellationToken cancellationToken);

    Task<InboundWebhookClaim> ClaimAsync(Guid connectionId, Guid merchantId, string pspCode,
        string externalEventId, string payloadFingerprint, WebhookVerificationMode mode,
        CancellationToken cancellationToken);

    /// <summary>Records a fetch-confirm-only webhook that arrived before its charge was bound (AC-8.4) and
    /// enqueues <c>InboundWebhookMatchRequested</c> in the same unit of work — atomically and exactly once
    /// per (connection, event id): a redelivery of the same event finds the existing pending row and does
    /// nothing (adversarial #6). Stores a bounded reference only, never a raw payload (AC-8.6).</summary>
    Task RecordPendingMatchAsync(Guid connectionId, Guid merchantId, string pspCode,
        string externalEventId, string externalChargeId, string payloadFingerprint,
        CancellationToken cancellationToken);

    Task<InboundWebhookEvent> LoadAsync(Guid eventId, CancellationToken cancellationToken);

    /// <summary>The pending-match rows for one bound charge — TRACKED, so the rematcher can complete them in
    /// its transaction. Empty once they are all resolved, which is what makes the rematch idempotent across
    /// both outbox directions (AC-8.4).</summary>
    Task<IReadOnlyList<InboundWebhookEvent>> FindPendingMatchesAsync(
        Guid connectionId, string externalChargeId, CancellationToken cancellationToken);
}
