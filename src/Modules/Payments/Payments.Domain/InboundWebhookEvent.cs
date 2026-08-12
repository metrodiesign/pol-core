namespace Payments.Domain;

public enum InboundWebhookStatus { Received = 1, Processed = 2, Duplicate = 3, Ignored = 4, Rejected = 5 }

public sealed class InboundWebhookEvent
{
    public Guid Id { get; private set; }
    public Guid PspConnectionId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid? PaymentSessionId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string PspCode { get; private set; } = default!;
    public string ExternalEventId { get; private set; } = default!;
    public string PayloadFingerprint { get; private set; } = default!;
    public bool SignatureValid { get; private set; }
    public InboundWebhookStatus Status { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTime ReceivedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public long Version { get; private set; }

    private InboundWebhookEvent() { }

    public static InboundWebhookEvent Receive(Guid connectionId, Guid merchantId, string pspCode,
        string externalEventId, string payloadFingerprint, DateTime now) => new()
        {
            Id = Guid.CreateVersion7(),
            PspConnectionId = connectionId,
            MerchantId = merchantId,
            PspCode = Required(pspCode, 32),
            ExternalEventId = Required(externalEventId, 256),
            PayloadFingerprint = Required(payloadFingerprint, 64),
            SignatureValid = true,
            Status = InboundWebhookStatus.Received,
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
        // redelivery of the same event must be allowed to replace Ignored with Processed.
        if (Status is not (InboundWebhookStatus.Received or InboundWebhookStatus.Ignored))
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
