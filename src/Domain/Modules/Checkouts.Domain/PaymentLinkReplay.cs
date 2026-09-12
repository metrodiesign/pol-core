using System.Security.Cryptography;
using SharedKernel;

namespace Checkouts.Domain;

/// <summary>
/// Safe replay pointer for link issuing. It stores request/link metadata only; raw token material is never
/// written to this row.
/// </summary>
public sealed class PaymentLinkReplay : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid? LinkId { get; private set; }
    public string Operation { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public byte[] RequestHash { get; private set; } = [];
    /// <summary>Data Protection ciphertext; never a raw token.</summary>
    public string? ProtectedRawToken { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    private PaymentLinkReplay() { }

    private PaymentLinkReplay(Guid id, Guid merchantId, Guid orderId, Guid? linkId, string operation,
        string idempotencyKey, byte[] requestHash, DateTime createdAt, DateTime expiresAt,
        string? protectedRawToken) : base(id)
    {
        if (merchantId == Guid.Empty || orderId == Guid.Empty || linkId == Guid.Empty)
            throw new ArgumentException("Replay identifiers are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (requestHash is null || requestHash.Length != 32)
            throw new ArgumentException("Replay request hash must be 32 bytes.", nameof(requestHash));
        if (linkId is not null && string.IsNullOrWhiteSpace(protectedRawToken))
            throw new ArgumentException("A link replay requires protected token material.", nameof(protectedRawToken));
        if (expiresAt <= createdAt)
            throw new ArgumentException("Replay expiry must be after creation.", nameof(expiresAt));
        MerchantId = merchantId;
        OrderId = orderId;
        LinkId = linkId;
        Operation = operation.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        RequestHash = requestHash.ToArray();
        ProtectedRawToken = string.IsNullOrWhiteSpace(protectedRawToken) ? null : protectedRawToken;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public static PaymentLinkReplay Create(Guid merchantId, Guid orderId, Guid? linkId, string operation,
        string idempotencyKey, ReadOnlySpan<byte> requestHash, DateTime createdAt, DateTime expiresAt,
        string? protectedRawToken = null) =>
        new(Guid.CreateVersion7(), merchantId, orderId, linkId, operation, idempotencyKey,
            requestHash.ToArray(), createdAt, expiresAt, protectedRawToken);

    public bool IsExpiredAt(DateTime now) => now >= ExpiresAt;

    public bool Matches(ReadOnlySpan<byte> requestHash) =>
        CryptographicOperations.FixedTimeEquals(RequestHash, requestHash);
}
