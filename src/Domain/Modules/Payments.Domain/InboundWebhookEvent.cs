using SharedKernel;

namespace Payments.Domain;

public enum InboundWebhookStatus { Received = 1, Processed = 2, Duplicate = 3, Ignored = 4, Rejected = 5, PendingMatch = 6 }

public sealed class InboundWebhookEvent
{
    public Guid Id { get; private set; }
    public Guid PspConnectionId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid? PaymentSessionId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string PspCode { get; private set; } = default!;
    public string ExternalEventId { get; private set; } = default!;

    /// <summary>The PSP charge the event refers to, bounded and untrusted (merchant-psp-settings AC-8.4).
    /// Set on a fetch-confirm-only event so a pending match can be re-driven by the charge id when the charge
    /// binds; a raw payload is NEVER stored (AC-8.6).</summary>
    public string? ExternalChargeId { get; private set; }
    public string PayloadFingerprint { get; private set; } = default!;

    /// <summary>Whether the body signature verified: <c>true</c> for a signed deterministic-reference event
    /// (2C2P) that passed, <c>false</c> for one that failed, and <c>null</c> when the provider is
    /// fetch-confirm-only (Omise) so the signature is not the authority (merchant-psp-settings design 481-482).</summary>
    public bool? SignatureValid { get; private set; }

    /// <summary>Which verification mode processed this event (design 481). Null on pre-existing rows.</summary>
    public WebhookVerificationMode? VerificationMode { get; private set; }
    public InboundWebhookStatus Status { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTime ReceivedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public long Version { get; private set; }

    private InboundWebhookEvent() { }

    public static InboundWebhookEvent Receive(Guid connectionId, Guid merchantId, string pspCode,
        string externalEventId, string payloadFingerprint, WebhookVerificationMode mode, DateTime now) => new()
        {
            Id = Guid.CreateVersion7(),
            PspConnectionId = connectionId,
            MerchantId = merchantId,
            PspCode = Required(pspCode, 32),
            ExternalEventId = Required(externalEventId, 256),
            PayloadFingerprint = Required(payloadFingerprint, 64),
            // A signed deterministic-reference event that reached Receive has already passed signature
            // verification; a fetch-confirm-only event's authenticity is the fetch, so its signature is not
            // an authority and is recorded as null (design 481-482).
            SignatureValid = mode == WebhookVerificationMode.SignedDeterministicReference ? true : null,
            VerificationMode = mode,
            Status = InboundWebhookStatus.Received,
            ReceivedAt = now,
            Version = 1,
        };

    /// <summary>A fetch-confirm-only webhook that arrived before its charge was bound to a session
    /// (AC-8.4): the bounded external charge id is kept so the charge-bound / match-requested outbox events
    /// can re-drive it, but no signature authority and no raw payload are stored.</summary>
    public static InboundWebhookEvent PendingMatch(Guid connectionId, Guid merchantId, string pspCode,
        string externalEventId, string externalChargeId, string payloadFingerprint, DateTime now) => new()
        {
            Id = Guid.CreateVersion7(),
            PspConnectionId = connectionId,
            MerchantId = merchantId,
            PspCode = Required(pspCode, 32),
            ExternalEventId = Required(externalEventId, 256),
            ExternalChargeId = Required(externalChargeId, 256),
            PayloadFingerprint = Required(payloadFingerprint, 64),
            SignatureValid = null,
            VerificationMode = WebhookVerificationMode.FetchConfirmOnly,
            Status = InboundWebhookStatus.PendingMatch,
            ReceivedAt = now,
            Version = 1,
        };

    public static InboundWebhookEvent Reject(Guid connectionId, Guid merchantId, string pspCode,
        string payloadFingerprint, bool signatureValid, string failureCode, DateTime now) => new()
        {
            Id = Guid.CreateVersion7(),
            PspConnectionId = connectionId,
            MerchantId = merchantId,
            PspCode = Required(pspCode, 32),
            ExternalEventId = $"rejected:{Required(payloadFingerprint, 64)}",
            PayloadFingerprint = payloadFingerprint,
            SignatureValid = signatureValid,
            Status = InboundWebhookStatus.Rejected,
            FailureCode = Required(failureCode, 64),
            ReceivedAt = now,
            ProcessedAt = now,
            Version = 1,
        };

    public void Complete(Guid paymentSessionId, Guid orderId, string outcome, DateTime now)
    {
        // Ignored is retryable: a PSP notification can arrive before payment inquiry settles. A later
        // redelivery of the same event must be allowed to replace Ignored with Processed. PendingMatch is
        // resolved the same way once its charge binds and the rematch fetch-confirms (AC-8.4).
        if (Status is not (InboundWebhookStatus.Received or InboundWebhookStatus.Ignored
            or InboundWebhookStatus.PendingMatch))
            throw new InvalidOperationException("Inbound webhook event is already terminal.");
        PaymentSessionId = paymentSessionId;
        OrderId = orderId;
        Status = outcome switch
        {
            "processed" => InboundWebhookStatus.Processed,
            "duplicate" => InboundWebhookStatus.Duplicate,
            _ => InboundWebhookStatus.Ignored,
        };
        ProcessedAt = now;
        Version++;
    }

    private static string Required(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value is required.");
        value = value.Trim();
        return value.Length <= maxLength
            ? value
            : throw new ArgumentException($"Value must not exceed {maxLength} characters.");
    }
}
