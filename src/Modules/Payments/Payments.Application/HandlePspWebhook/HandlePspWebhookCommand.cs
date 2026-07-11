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

    /// <summary>A first-time, confirmed event that drove a state transition + outbox enqueue.</summary>
    Processed = 1,

    /// <summary>A replay/duplicate of an already-claimed event — safely ignored.</summary>
    Duplicate = 2,

    /// <summary>Verified and first-seen, but the fetch-to-confirm did not yet confirm a paid charge.</summary>
    Ignored = 3,
}

/// <summary>The result of the webhook command.</summary>
public sealed record WebhookHandled(WebhookOutcome Outcome);
