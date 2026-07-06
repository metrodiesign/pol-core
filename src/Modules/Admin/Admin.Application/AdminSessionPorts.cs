using Admin.Domain;

namespace Admin.Application;

/// <summary>
/// Persistence seam for admin BFF sessions (REQ-3/5/6/11). Racing transitions (rotate/revoke/slide) are
/// expressed as atomic set-based updates so concurrent requests cannot lost-update a session's status — the
/// affected-row count of <see cref="TrySupersedeAsync"/> is the single-winner flag for rotation (REQ-5.5).
/// </summary>
public interface IAdminSessionStore
{
    /// <summary>Looks up a session by the SHA-256 hash of the presented cookie token. O(1) (unique index).</summary>
    Task<AdminSession?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>The id of the family's single Active session, or null when there is not exactly one — used for
    /// immediate-predecessor (reuse) detection (REQ-5.2/5.3).</summary>
    Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken cancellationToken);

    void Add(AdminSession session);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Atomically marks an Active session Superseded (linking its successor) via a single conditional
    /// UPDATE; returns true only for the winning caller (affected rows &gt; 0), so concurrent rotations produce
    /// exactly one successor (REQ-5.5).</summary>
    Task<bool> TrySupersedeAsync(Guid sessionId, Guid successorId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Slides the idle expiry forward for a still-Active session (REQ-3.5), lazily.</summary>
    Task SlideIdleAsync(Guid sessionId, DateTime idleExpiresAt, CancellationToken cancellationToken);

    /// <summary>Revokes every non-revoked session in a family (reuse detection / logout — REQ-5.3/6.1).</summary>
    Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken);

    /// <summary>Revokes every non-revoked session of an admin across all devices (logout-all — REQ-6.2).</summary>
    Task RevokeAllForAdminAsync(Guid adminAccountId, CancellationToken cancellationToken);

    /// <summary>Deletes sessions past their absolute expiry so the store does not grow unbounded (REQ-11.5);
    /// returns the number removed.</summary>
    Task<int> PruneAsync(DateTime now, CancellationToken cancellationToken);

    /// <summary>All of an admin's stored sessions, newest first with a session-id tiebreak (unpaged — the prune
    /// job bounds the set), for the session-management view (admin-account-management REQ-4). Read-only.</summary>
    Task<IReadOnlyList<AdminSession>> ListByAdminAsync(Guid adminAccountId, CancellationToken cancellationToken);

    /// <summary>Looks up a single session by id (admin-account-management REQ-5) — used to check ownership and read
    /// its <see cref="AdminSession.FamilyId"/> before a family revoke. Read-only.</summary>
    Task<AdminSession?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken);
}

/// <summary>Append-only auth event audit (REQ-12). Separate writer from <see cref="IAdminAccountAuditWriter"/>
/// because an auth event may carry no resolved admin id, which the account audit forbids.</summary>
public interface IAdminAuthAuditWriter
{
    void Append(AdminAuthAudit entry);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
