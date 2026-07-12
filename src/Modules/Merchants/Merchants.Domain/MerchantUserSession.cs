using SharedKernel;

namespace Merchants.Domain;

public enum MerchantUserSessionStatus
{
    Active = 0,
    Superseded = 1,
    Revoked = 2,
}

/// <summary>Session lifetime knobs the domain needs. A pure value object — the host binds it from
/// <c>MerchantUser:Session:*</c> config so the domain stays config-free.</summary>
public sealed record MerchantUserSessionPolicy(TimeSpan Idle, TimeSpan Absolute, TimeSpan Rotation, TimeSpan Grace);

/// <summary>
/// A server-side merchant-user session for the BFF. The opaque cookie value is NEVER stored — only its SHA-256 hash.
/// Sessions form a rotation family: each <see cref="Rotate"/> issues a successor in the same
/// <see cref="FamilyId"/> and marks the predecessor <see cref="MerchantUserSessionStatus.Superseded"/> with a link to
/// its successor, so replay of a non-immediate predecessor is detectable as theft. Racing transitions
/// (rotate/revoke) are applied in the store via atomic set-based updates, not by mutating a tracked entity.
/// </summary>
// ponytail: DUPLICATE of Admin.Domain.AdminSession (owner AdminAccountId -> MerchantUserId) — deliberate debt, do not refactor into a shared base.
public sealed class MerchantUserSession : AggregateRoot<Guid>
{
    public Guid FamilyId { get; private set; }
    public byte[] TokenHash { get; private set; } = default!;
    public Guid MerchantUserId { get; private set; }
    public MerchantUserSessionStatus Status { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime IdleExpiresAt { get; private set; }
    public DateTime AbsoluteExpiresAt { get; private set; }
    public DateTime? SupersededAt { get; private set; }
    public Guid? SupersededBySessionId { get; private set; }
    public string? CreatedIp { get; private set; }
    public string? UserAgent { get; private set; }

    private MerchantUserSession() { } // EF materialisation

    private MerchantUserSession(Guid id, Guid familyId, byte[] tokenHash, Guid merchantUserId, DateTime issuedAt,
        DateTime idleExpiresAt, DateTime absoluteExpiresAt, string? createdIp, string? userAgent) : base(id)
    {
        FamilyId = familyId;
        TokenHash = tokenHash;
        MerchantUserId = merchantUserId;
        Status = MerchantUserSessionStatus.Active;
        IssuedAt = issuedAt;
        IdleExpiresAt = idleExpiresAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
        CreatedIp = createdIp;
        UserAgent = userAgent;
    }

    /// <summary>Opens a NEW session family at login. Idle + absolute expiry are measured from now.</summary>
    public static MerchantUserSession Start(Guid merchantUserId, byte[] tokenHash, DateTime now, MerchantUserSessionPolicy policy,
        string? createdIp = null, string? userAgent = null)
    {
        if (merchantUserId == Guid.Empty)
            throw new ArgumentException("MerchantUserId is required.", nameof(merchantUserId));
        RequireHash(tokenHash);
        return new MerchantUserSession(Guid.NewGuid(), Guid.NewGuid(), tokenHash, merchantUserId, now,
            now + policy.Idle, now + policy.Absolute, createdIp, userAgent);
    }

    /// <summary>Issues a successor in the SAME family and marks THIS session Superseded with a link to it. The
    /// successor inherits the family's absolute expiry — rotation never extends the hard cap; only idle slides
    /// forward. The predecessor's status flip is also persisted atomically in the store (TrySupersede); this
    /// in-memory mutation produces the returned object graph and unit-tests.</summary>
    public MerchantUserSession Rotate(byte[] newHash, DateTime now, MerchantUserSessionPolicy policy)
    {
        RequireHash(newHash);
        var successor = new MerchantUserSession(Guid.NewGuid(), FamilyId, newHash, MerchantUserId, now,
            now + policy.Idle, AbsoluteExpiresAt, CreatedIp, UserAgent);
        Status = MerchantUserSessionStatus.Superseded;
        SupersededAt = now;
        SupersededBySessionId = successor.Id;
        return successor;
    }

    /// <summary>Active and within BOTH the idle and absolute windows.</summary>
    public bool IsLiveAt(DateTime now) =>
        Status == MerchantUserSessionStatus.Active && now < IdleExpiresAt && now < AbsoluteExpiresAt;

    /// <summary>True when THIS superseded session is the immediate predecessor of the family's current Active
    /// session AND still inside the grace window — a legitimate in-flight request lagging one rotation.
    /// A superseded session that is NOT the immediate predecessor (replayed more than one rotation
    /// back, or a fork) or is past grace is reuse/theft, decided by the caller.</summary>
    public bool IsImmediatePredecessorWithinGrace(Guid familyActiveSessionId, DateTime now, TimeSpan grace) =>
        Status == MerchantUserSessionStatus.Superseded
        && SupersededBySessionId == familyActiveSessionId
        && SupersededAt is { } supersededAt
        && now <= supersededAt + grace;

    private static void RequireHash(byte[] tokenHash)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        if (tokenHash.Length != 32) // SHA-256 digest
            throw new ArgumentException("TokenHash must be a 32-byte SHA-256 digest.", nameof(tokenHash));
    }
}
