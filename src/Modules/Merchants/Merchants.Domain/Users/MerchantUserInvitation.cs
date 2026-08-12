using SharedKernel;

namespace Merchants.Domain.Users;

public enum InvitationActorAudience
{
    Merchant = 1,
    Admin = 2,
}

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
    public InvitationActorAudience CreatedByAudience { get; private set; }
    public string IntendedRoleCodesJson { get; private set; } = "[]";
    public DateTime CreatedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private MerchantUserInvitation() { }

    private MerchantUserInvitation(Guid id, Guid merchantId, string email, string tokenHash,
        DateTime expiresAt, Guid createdByUserId, InvitationActorAudience createdByAudience,
        string intendedRoleCodesJson, DateTime createdAt) : base(id)
    {
        MerchantId = merchantId;
        Email = email;
        NormalizedEmail = NormalizeEmail(email);
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedByUserId = createdByUserId;
        CreatedByAudience = createdByAudience;
        IntendedRoleCodesJson = intendedRoleCodesJson;
        CreatedAt = createdAt;
    }

    public static MerchantUserInvitation Create(Guid merchantId, string email, string tokenHash,
        DateTime expiresAt, Guid createdByUserId, DateTime now,
        InvitationActorAudience createdByAudience = InvitationActorAudience.Merchant,
        IReadOnlyList<string>? intendedRoleCodes = null)
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
        if (createdByAudience is not InvitationActorAudience.Merchant and not InvitationActorAudience.Admin)
            throw new ArgumentOutOfRangeException(nameof(createdByAudience));
        var roles = (intendedRoleCodes ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var roleJson = System.Text.Json.JsonSerializer.Serialize(roles);
        if (roleJson.Length > 2_000)
            throw new ArgumentException("Invitation role metadata is too large.", nameof(intendedRoleCodes));
        return new MerchantUserInvitation(Guid.CreateVersion7(), merchantId, trimmed, tokenHash, expiresAt,
            createdByUserId, createdByAudience, roleJson, now);
    }

    public IReadOnlyList<string> IntendedRoleCodes() =>
        System.Text.Json.JsonSerializer.Deserialize<string[]>(IntendedRoleCodesJson) ?? [];

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
