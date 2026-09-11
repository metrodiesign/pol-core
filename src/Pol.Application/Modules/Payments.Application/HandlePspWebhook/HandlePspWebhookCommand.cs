using Mediator;

namespace Payments.Application.HandlePspWebhook;

/// <summary>
/// The critical inbound webhook path. The connection is resolved by <see cref="PspConnectionId"/>
/// (NOT from anything parsed out of the raw URL before the signature is verified — security rules).
/// NOT merchant-scoped: the request arrives unauthenticated from the PSP; the merchant is derived from the
/// trusted connection record after the signature is verified.
/// </summary>
public sealed record HandlePspWebhookCommand(
    Guid PspConnectionId,
    string RawPayload,
    string Signature) : ICommand<WebhookHandled>;

/// <summary>Outcome of processing a PSP webhook.</summary>
public enum WebhookOutcome
{
    /// <summary>Signature verification failed — the payload was not trusted and nothing was changed.</summary>
    Rejected = 0,

    /// <summary>A first-time, confirmed event that drove a state transition (Paid — which also enqueues
    /// <c>PaymentPaid</c> — or the Failed/Expired the confirmation earned instead).</summary>
    Processed = 1,

    /// <summary>A replay/duplicate of an already-claimed event — safely ignored.</summary>
    Duplicate = 2,

    /// <summary>Verified and first-seen, but the fetch-to-confirm did not yet confirm a paid charge.</summary>
    Ignored = 3,

    /// <summary>A fetch-confirm-only (Omise) webhook arrived before its charge was bound to a session: a
    /// bounded pending match was recorded and a rematch enqueued. Answered 202 <c>webhook_pending_match</c>
    /// (merchant-psp-settings AC-8.4).</summary>
    PendingMatch = 4,

    /// <summary>The session that pins the secret could not be resolved-and-verified yet — a signed webhook
    /// whose charge is not bound, or a pinned secret version that is momentarily unreadable (rotation/lease
    /// mid-flight). Nothing was accepted or acked; the provider retries. Answered 503
    /// <c>webhook_verification_deferred</c> (merchant-psp-settings AC-8.5).</summary>
    Deferred = 5,
}

/// <summary>The result of the webhook command.</summary>
public sealed record WebhookHandled(WebhookOutcome Outcome);
