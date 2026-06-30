using SharedKernel;

namespace Producer.Domain;

public enum ProducerSessionStatus
{
    Active = 0,
    Superseded = 1,
    Revoked = 2,
}

/// <summary>Session lifetime knobs the domain needs (REQ-10/11). A pure value object — the host binds it from
/// <c>ProducerSession:*</c> config so the domain stays config-free.</summary>
public sealed record ProducerSessionPolicy(TimeSpan Idle, TimeSpan Absolute, TimeSpan Rotation, TimeSpan Grace);

/// <summary>
/// A server-side producer session for the BFF. The opaque cookie value is NEVER stored — only its SHA-256 hash
/// (REQ-10.1). Sessions form a rotation family: each <see cref="Rotate"/> issues a successor in the same
/// <see cref="FamilyId"/> and marks the predecessor <see cref="ProducerSessionStatus.Superseded"/> with a link to
/// its successor, so replay of a non-immediate predecessor is detectable as theft (REQ-11). Racing transitions
/// (rotate/revoke) are applied in the store via atomic set-based updates, not by mutating a tracked entity.
/// </summary>
// ponytail: DUPLICATE of Admin.Domain.AdminSession (owner AdminAccountId -> ProducerAccountId) — deliberate debt, do not refactor into a shared base.
public sealed class ProducerSession : AggregateRoot<Guid>
{
    public Guid FamilyId { get; private set; }
    public byte[] TokenHash { get; private set; } = default!;
    public Guid ProducerAccountId { get; private set; }
    public ProducerSessionStatus Status { get; private set; }
    public DateTime IssuedAt { get; private set; }
    public DateTime IdleExpiresAt { get; private set; }
    public DateTime AbsoluteExpiresAt { get; private set; }
    public DateTime? SupersededAt { get; private set; }
    public Guid? SupersededBySessionId { get; private set; }
    public string? CreatedIp { get; private set; }
    public string? UserAgent { get; private set; }

    private ProducerSession() { } // EF materialisation

    private ProducerSession(Guid id, Guid familyId, byte[] tokenHash, Guid producerAccountId, DateTime issuedAt,
        DateTime idleExpiresAt, DateTime absoluteExpiresAt, string? createdIp, string? userAgent) : base(id)
    {
        FamilyId = familyId;
        TokenHash = tokenHash;
        ProducerAccountId = producerAccountId;
        Status = ProducerSessionStatus.Active;
        IssuedAt = issuedAt;
        IdleExpiresAt = idleExpiresAt;
        AbsoluteExpiresAt = absoluteExpiresAt;
        CreatedIp = createdIp;
        UserAgent = userAgent;
    }

    /// <summary>Opens a NEW session family at login (REQ-10.1). Idle + absolute expiry are measured from now.</summary>
    public static ProducerSession Start(Guid producerAccountId, byte[] tokenHash, DateTime now, ProducerSessionPolicy policy,
        string? createdIp = null, string? userAgent = null)
    {
        if (producerAccountId == Guid.Empty)
            throw new ArgumentException("ProducerAccountId is required.", nameof(producerAccountId));
        RequireHash(tokenHash);
        return new ProducerSession(Guid.NewGuid(), Guid.NewGuid(), tokenHash, producerAccountId, now,
            now + policy.Idle, now + policy.Absolute, createdIp, userAgent);
    }

    /// <summary>Issues a successor in the SAME family and marks THIS session Superseded with a link to it
    /// (REQ-11.1). The successor inherits the family's absolute expiry — rotation never extends the hard cap
    /// (REQ-10.4); only idle slides forward. The predecessor's status flip is also persisted atomically in the
    /// store (TrySupersede); this in-memory mutation produces the returned object graph and unit-tests.</summary>
    public ProducerSession Rotate(byte[] newHash, DateTime now, ProducerSessionPolicy policy)
    {
        RequireHash(newHash);
        var successor = new ProducerSession(Guid.NewGuid(), FamilyId, newHash, ProducerAccountId, now,
            now + policy.Idle, AbsoluteExpiresAt, CreatedIp, UserAgent);
        Status = ProducerSessionStatus.Superseded;
        SupersededAt = now;
        SupersededBySessionId = successor.Id;
        return successor;
    }

    /// <summary>Active and within BOTH the idle and absolute windows (REQ-10.4).</summary>
    public bool IsLiveAt(DateTime now) =>
        Status == ProducerSessionStatus.Active && now < IdleExpiresAt && now < AbsoluteExpiresAt;

    /// <summary>True when THIS superseded session is the immediate predecessor of the family's current Active
    /// session AND still inside the grace window — a legitimate in-flight request lagging one rotation
    /// (REQ-11.2). A superseded session that is NOT the immediate predecessor (replayed more than one rotation
    /// back, or a fork) or is past grace is reuse/theft (REQ-11.3), decided by the caller.</summary>
    public bool IsImmediatePredecessorWithinGrace(Guid familyActiveSessionId, DateTime now, TimeSpan grace) =>
        Status == ProducerSessionStatus.Superseded
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
