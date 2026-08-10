using SharedKernel;

namespace Merchants.Domain.Users;

public sealed class MerchantUserInvitation : Entity<Guid>
{
    public Guid MerchantId { get; private set; }
    public string Email { get; private set; } = default!;
    public string NormalizedEmail { get; private set; } = default!;
    public string TokenHash { get; private set; } = default!;
    public DateTime ExpiresAt { get; private set; }
    public DateTime? AcceptedAt { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTime? RevokedAt { get; private set; }
    public Guid CreatedByUserId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private MerchantUserInvitation() { }

    private MerchantUserInvitation(Guid id, Guid merchantId, string email, string tokenHash,
        DateTime expiresAt, Guid createdByUserId, DateTime createdAt) : base(id)
    {
        MerchantId = merchantId;
        Email = email;
        NormalizedEmail = NormalizeEmail(email);
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedByUserId = createdByUserId;
        CreatedAt = createdAt;
    }

    public static MerchantUserInvitation Create(Guid merchantId, string email, string tokenHash,
        DateTime expiresAt, Guid createdByUserId, DateTime now)
    {
        if (merchantId == Guid.Empty || createdByUserId == Guid.Empty)
            throw new ArgumentException("Merchant and actor are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        var trimmed = email.Trim();
        var at = trimmed.IndexOf('@');
        if (trimmed.Length > 320 || at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1
            || trimmed.Any(char.IsWhiteSpace))
            throw new ArgumentException("A valid invitation email is required.", nameof(email));
        if (expiresAt <= now)
            throw new ArgumentException("Invitation expiry must be in the future.", nameof(expiresAt));
        return new MerchantUserInvitation(Guid.CreateVersion7(), merchantId, trimmed, tokenHash, expiresAt, createdByUserId, now);
    }

    public bool IsPending(DateTime now) => AcceptedAt is null && RevokedAt is null && now < ExpiresAt;

    public void Revoke(DateTime now)
    {
        if (AcceptedAt is not null)
            throw new InvalidOperationException("An accepted invitation cannot be revoked.");
        RevokedAt ??= now;
    }

    public void Accept(Guid userId, string verifiedEmail, DateTime now)
    {
        if (!IsPending(now))
            throw new InvalidOperationException("The invitation is no longer usable.");
        if (!string.Equals(NormalizedEmail, NormalizeEmail(verifiedEmail), StringComparison.Ordinal))
            throw new InvalidOperationException("The verified email does not match the invitation.");
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));
        AcceptedByUserId = userId;
        AcceptedAt = now;
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
