using Merchants.Domain;
using Merchants.Domain.Users;

namespace Merchants.Application.Users;

/// <summary>
/// Persistence seam for merchant-user BFF sessions (REQ-10/11/12). Racing transitions (rotate/revoke/slide) are
/// expressed as atomic set-based updates so concurrent requests cannot lost-update a session's status — the
/// affected-row count of <see cref="TrySupersedeAsync"/> is the single-winner flag for rotation (REQ-11.5).
/// </summary>
public interface ISessionStore
{
    /// <summary>Looks up a session by the SHA-256 hash of the presented cookie token. O(1) (unique index).</summary>
    Task<Session?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>The id of the family's single Active session, or null when there is not exactly one — used for
    /// immediate-predecessor (reuse) detection (REQ-11.2/11.3).</summary>
    Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken cancellationToken);

    void Add(Session session);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>Atomically marks an Active session Superseded (linking its successor) via a single conditional
    /// UPDATE; returns true only for the winning caller (affected rows &gt; 0), so concurrent rotations produce
    /// exactly one successor (REQ-11.5).</summary>
    Task<bool> TrySupersedeAsync(Guid sessionId, Guid successorId, DateTime now, CancellationToken cancellationToken);

    /// <summary>Slides the idle expiry forward for a still-Active session (REQ-10.4), lazily.</summary>
    Task SlideIdleAsync(Guid sessionId, DateTime idleExpiresAt, CancellationToken cancellationToken);

    /// <summary>Revokes every non-revoked session in a family (reuse detection / logout — REQ-11.3/12.1).</summary>
    Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken);

    /// <summary>Revokes every non-revoked session of a User across all devices (logout-all + suspend
    /// propagation — REQ-12.2/12.3).</summary>
    Task RevokeAllForUserAsync(Guid merchantUserId, CancellationToken cancellationToken);

    /// <summary>Deletes sessions past their absolute expiry so the store does not grow unbounded (REQ-10.4);
    /// returns the number removed.</summary>
    Task<int> PruneAsync(DateTime now, CancellationToken cancellationToken);
}

/// <summary>Append-only auth event audit (REQ-12/21). A separate writer because an auth event may carry no
/// resolved User id.</summary>
public interface IAuthAuditWriter
{
    void Append(AuthAudit entry);
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
