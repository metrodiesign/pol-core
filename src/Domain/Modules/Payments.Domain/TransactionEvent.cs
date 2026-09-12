using SharedKernel;

namespace Payments.Domain;

/// <summary>Append-only provider evidence for a Transaction. Raw payloads and secrets are never stored.</summary>
public sealed class TransactionEvent : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid TransactionId { get; private set; }
    public string Source { get; private set; } = default!;
    public string EventReference { get; private set; } = default!;
    public TransactionStatus? Status { get; private set; }
    public string? ProviderStatus { get; private set; }
    public string? EvidenceCode { get; private set; }
    public string? SafeDetails { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public DateTime ReceivedAt { get; private set; }

    private TransactionEvent() { }

    private TransactionEvent(
        Guid id,
        Guid merchantId,
        Guid transactionId,
        string source,
        string eventReference,
        TransactionStatus? status,
        string? providerStatus,
        string? evidenceCode,
        string? safeDetails,
        DateTime occurredAt,
        DateTime receivedAt) : base(id)
    {
        MerchantId = merchantId;
        TransactionId = transactionId;
        Source = source;
        EventReference = eventReference;
        Status = status;
        ProviderStatus = providerStatus;
        EvidenceCode = evidenceCode;
        SafeDetails = safeDetails;
        OccurredAt = occurredAt;
        ReceivedAt = receivedAt;
    }

    public static TransactionEvent Create(
        Guid merchantId,
        Guid transactionId,
        string source,
        string eventReference,
        TransactionStatus? status,
        string? providerStatus,
        string? evidenceCode,
        string? safeDetails,
        DateTime occurredAt,
        DateTime receivedAt)
    {
        if (merchantId == Guid.Empty || transactionId == Guid.Empty)
            throw new ArgumentException("Transaction event binding is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventReference);
        return new TransactionEvent(
            Guid.CreateVersion7(), merchantId, transactionId, BoundRequired(source, 64), BoundRequired(eventReference, 256),
            status, Bound(providerStatus, 128), Bound(evidenceCode, 128), Bound(safeDetails, 2000),
            occurredAt, receivedAt);
    }

    private static string? Bound(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength
            ? value.Trim() : value.Trim()[..maxLength];

    private static string BoundRequired(string value, int maxLength) =>
        Bound(value, maxLength) ?? throw new ArgumentException("A bounded event reference is required.");
}
