namespace SharedKernel;

/// <summary>
/// How a PSP's inbound webhook proves authenticity before any state transition (merchant-psp-settings
/// design §5). Lives in SharedKernel because both the Payments adapter seam (which declares its mode) and
/// the <c>InboundWebhookEvent</c> domain entity (which records which mode processed an event) need it, and
/// neither may reference the other. Persisted as int.
/// </summary>
public enum WebhookVerificationMode
{
    /// <summary>2C2P: the body carries an HS256-signed JWT and <c>invoiceNo</c> is the deterministic
    /// <c>Session.Id</c>, so the session is resolved from the reference and the signature is verified with
    /// the version the session pinned BEFORE the event is accepted.</summary>
    SignedDeterministicReference = 1,

    /// <summary>Omise: the body carries no verifiable signature over the (payload, signature, secret) seam,
    /// so the only authority is a server-side fetch-to-confirm with the pinned secret. A webhook that arrives
    /// before its charge is bound is parked as a pending match and re-driven when the bind lands.</summary>
    FetchConfirmOnly = 2,
}
