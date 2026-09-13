using System.Security.Cryptography;
using SharedKernel;

namespace Checkouts.Domain;

public enum PaymentLinkStatus
{
    Active = 1,
    Revoked = 2,
    Expired = 3,
}

/// <summary>
/// Capability link ของ Order. เก็บเฉพาะ keyed token hash จึงไม่มี raw token หรือ business snapshot อยู่ใน row.
/// </summary>
public sealed class PaymentLink : Entity<Guid>
{
    public Guid OrderId { get; private set; }
    public Guid MerchantId { get; private set; }
    public byte[] TokenHash { get; private set; } = [];
    public PaymentLinkStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public Guid? RotatedFromLinkId { get; private set; }
    public long Version { get; private set; }

    private PaymentLink() { }

    private PaymentLink(Guid id, Guid orderId, Guid merchantId, byte[] tokenHash,
        DateTime createdAt, DateTime expiresAt, Guid? rotatedFromLinkId) : base(id)
    {
        if (orderId == Guid.Empty)
            throw new ArgumentException("OrderId is required.", nameof(orderId));
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        if (tokenHash is null || tokenHash.Length != 32)
            throw new ArgumentException("Payment link token hash must be a SHA-256 sized digest.", nameof(tokenHash));
        if (expiresAt <= createdAt)
            throw new ArgumentException("Payment link expiry must be after creation.", nameof(expiresAt));
        OrderId = orderId;
        MerchantId = merchantId;
        TokenHash = tokenHash.ToArray();
        Status = PaymentLinkStatus.Active;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        RotatedFromLinkId = rotatedFromLinkId;
        Version = 1;
    }

    public static PaymentLink Create(Guid orderId, Guid merchantId, ReadOnlySpan<byte> tokenHash,
        DateTime createdAt, DateTime expiresAt, Guid? rotatedFromLinkId = null) =>
        new(Guid.CreateVersion7(), orderId, merchantId, tokenHash.ToArray(), createdAt, expiresAt, rotatedFromLinkId);

    public bool IsActiveAt(DateTime now) => Status == PaymentLinkStatus.Active && now < ExpiresAt;

    public bool MatchesHash(ReadOnlySpan<byte> tokenHash) =>
        CryptographicOperations.FixedTimeEquals(TokenHash, tokenHash);

    public void Revoke(DateTime now)
    {
        if (Status == PaymentLinkStatus.Revoked)
            return;
        Status = PaymentLinkStatus.Revoked;
        RevokedAt = now;
        Version++;
    }

    public void Expire(DateTime now)
    {
        if (Status != PaymentLinkStatus.Active || now < ExpiresAt)
            return;
        Status = PaymentLinkStatus.Expired;
        Version++;
    }
}
