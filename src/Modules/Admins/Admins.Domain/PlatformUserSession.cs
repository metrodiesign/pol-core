using SharedKernel;

namespace Admins.Domain;

public enum PlatformUserSessionStatus
{
    Active = 0,
    Superseded = 1,
    Revoked = 2,
}

/// <summary>Session lifetime knobs the domain needs (REQ-3/5). A pure value object — the host binds it from
/// <c>PlatformUserSession:*</c> config so the domain stays config-free.</summary>
public sealed record PlatformUserSessionPolicy(TimeSpan Idle, TimeSpan Absolute, TimeSpan Rotation, TimeSpan Grace);

/// <summary>
/// A server-side admin session for the BFF. The opaque cookie value is NEVER stored — only its SHA-256 hash
/// (REQ-11.2). Sessions form a rotation family: each <see cref="Rotate"/> issues a successor in the same
/// <see cref="FamilyId"/> and marks the predecessor <see cref="PlatformUserSessionStatus.Superseded"/> with a link to
/// its successor, so replay of a non-immediate predecessor is detectable as theft (REQ-5). Racing transitions
/// (rotate/revoke) are applied in the store via atomic set-based updates, not by mutating a tracked entity.
/// </summary>
public sealed class PlatformUserSession : AggregateRoot<Guid>
{
    public Guid FamilyId { get; private set; }
    public byte[] TokenHash { get; private set; } = default!;
    public Guid PlatformUserId { get; private set; }
    public PlatformUserSessionStatus Status { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime IdleExpiresAt { get; private set; }
    public DateTime AbsoluteExpiresAt { get; private set; }
    public DateTime? SupersededAt { get; private set; }
    public Guid? SupersededBySessionId { get; private set; }
    public string? CreatedIp { get; private set; }
    public string? UserAgent { get; private set; }

    private PlatformUserSession() { } // EF materialisation

    private PlatformUserSession(Guid id, Guid familyId, byte[] tokenHash, Guid adminAccountId, DateTime issuedAt,
        DateTime idleExpiresAt, DateTime absoluteExpiresAt, string? createdIp, string? userAgent) : base(id)
    {
        FamilyId = familyId;
        TokenHash = tokenHash;
        PlatformUserId = adminAccountId;
        Status = PlatformUserSessionStatus.Active;
        IssuedAt = issuedAt;
        IdleExpiresAt = idleExpiresAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
        CreatedIp = createdIp;
        UserAgent = userAgent;
    }

    /// <summary>Opens a NEW session family at login (REQ-3.1). Idle + absolute expiry are measured from now.</summary>
    public static PlatformUserSession Start(Guid adminAccountId, byte[] tokenHash, DateTime now, PlatformUserSessionPolicy policy,
        string? createdIp = null, string? userAgent = null)
    {
        if (adminAccountId == Guid.Empty)
            throw new ArgumentException("PlatformUserId is required.", nameof(adminAccountId));
        RequireHash(tokenHash);
        return new PlatformUserSession(Guid.NewGuid(), Guid.NewGuid(), tokenHash, adminAccountId, now,
            now + policy.Idle, now + policy.Absolute, createdIp, userAgent);
    }

    /// <summary>Issues a successor in the SAME family and marks THIS session Superseded with a link to it
    /// (REQ-5.1). The successor inherits the family's absolute expiry — rotation never extends the hard cap
    /// (REQ-3.4/3.5); only idle slides forward. The predecessor's status flip is also persisted atomically in
    /// the store (TrySupersede); this in-memory mutation produces the returned object graph and unit-tests.</summary>
    public PlatformUserSession Rotate(byte[] newHash, DateTime now, PlatformUserSessionPolicy policy)
    {
        RequireHash(newHash);
        var successor = new PlatformUserSession(Guid.NewGuid(), FamilyId, newHash, PlatformUserId, now,
            now + policy.Idle, AbsoluteExpiresAt, CreatedIp, UserAgent);
        Status = PlatformUserSessionStatus.Superseded;
        SupersededAt = now;
        SupersededBySessionId = successor.Id;
        return successor;
    }

    /// <summary>Active and within BOTH the idle and absolute windows (REQ-3.4).</summary>
    public bool IsLiveAt(DateTime now) =>
        Status == PlatformUserSessionStatus.Active && now < IdleExpiresAt && now < AbsoluteExpiresAt;

    /// <summary>True when THIS superseded session is the immediate predecessor of the family's current Active
    /// session AND still inside the grace window — a legitimate in-flight request lagging one rotation
    /// (REQ-5.2). A superseded session that is NOT the immediate predecessor (replayed more than one rotation
    /// back, or a fork) or is past grace is reuse/theft (REQ-5.3), decided by the caller.</summary>
    public bool IsImmediatePredecessorWithinGrace(Guid familyActiveSessionId, DateTime now, TimeSpan grace) =>
        Status == PlatformUserSessionStatus.Superseded
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
