using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// EF-backed admin session store over <see cref="ControlPlaneDbContext"/> — moved from
/// <c>Admins.Infrastructure.Persistence.Users.SessionStore</c> (task 8.5.1), unchanged. Reads are AsNoTracking;
/// every status transition that can race (rotate/revoke/slide) is a single set-based <c>ExecuteUpdate</c> so two
/// concurrent requests cannot lost-update — the affected-row count of <see cref="TrySupersedeAsync"/> is the
/// single-winner flag for rotation (REQ-5.5).
/// </summary>
internal sealed class SessionStore : ISessionStore
{
    private readonly ControlPlaneDbContext _db;
    private readonly ISecurityTelemetry _telemetry;

    public SessionStore(ControlPlaneDbContext db, ISecurityTelemetry telemetry)
    {
        _db = db;
        _telemetry = telemetry;
    }

    public Task<Session?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public async Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken cancellationToken)
    {
        // The family invariant is at most one Active session; if a bug ever yields more, treat it as "no single
        // active" so the immediate-predecessor (reuse) check fails closed rather than picking an arbitrary id.
        var activeIds = await _db.Sessions.AsNoTracking()
            .Where(s => s.FamilyId == familyId && s.Status == SessionStatus.Active)
            .Select(s => s.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        return activeIds.Count == 1 ? activeIds[0] : null;
    }

    public void Add(Session session) => _db.Sessions.Add(session);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public async Task<bool> TrySupersedeAsync(Guid sessionId, Guid successorId, DateTime now, CancellationToken cancellationToken)
    {
        var affected = await _db.Sessions
            .Where(s => s.Id == sessionId && s.Status == SessionStatus.Active)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, SessionStatus.Superseded)
                .SetProperty(s => s.SupersededAt, now)
                .SetProperty(s => s.SupersededBySessionId, successorId), cancellationToken);

        // 0 rows means the session was not Active when this ran (already rotated/revoked by a concurrent
        // request, or a replay of a stale token) — the single-winner flag missed, worth its own category
        // (REQ-13.1 "cardinality anomaly").
        if (affected == 0)
            _telemetry.Emit(new DenialEvent(
                DenialCategory.PortCardinalityAnomaly, "admin", ActorId: null, TargetMerchant: null,
                nameof(Session), "TrySupersede", "Session rotation affected 0 rows; the session was not Active.",
                CorrelationId.Current, DateTime.UtcNow));

        return affected > 0;
    }

    public Task SlideIdleAsync(Guid sessionId, DateTime idleExpiresAt, CancellationToken cancellationToken) =>
        _db.Sessions
            .Where(s => s.Id == sessionId && s.Status == SessionStatus.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.IdleExpiresAt, idleExpiresAt), cancellationToken);

    public Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        _db.Sessions
            .Where(s => s.FamilyId == familyId && s.Status != SessionStatus.Revoked)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, SessionStatus.Revoked), cancellationToken);

    public Task RevokeAllForAdminAsync(Guid adminAccountId, CancellationToken cancellationToken) =>
        _db.Sessions
            .Where(s => s.PlatformUserId == adminAccountId && s.Status != SessionStatus.Revoked)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, SessionStatus.Revoked), cancellationToken);

    // Every session carries an absolute expiry <= 8h out, so deleting past-absolute rows bounds the table for
    // expired AND revoked sessions alike (a revoked session is gone within its remaining absolute lifetime).
    public Task<int> PruneAsync(DateTime now, CancellationToken cancellationToken) =>
        _db.Sessions.Where(s => s.AbsoluteExpiresAt < now).ExecuteDeleteAsync(cancellationToken);

    // admin-account-management REQ-4.1: newest first + id tiebreak, unpaged (prune bounds the set). AsNoTracking —
    // a read for the console.
    public async Task<IReadOnlyList<Session>> ListByAdminAsync(Guid adminAccountId, CancellationToken cancellationToken) =>
        await _db.Sessions.AsNoTracking()
            .Where(s => s.PlatformUserId == adminAccountId)
            .OrderByDescending(s => s.IssuedAt).ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

    // admin-account-management REQ-5: read one session (ownership check + FamilyId) before a family revoke.
    public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
}

/// <summary>Append-only writer for <c>admin.AuthAudits</c> (REQ-12.2) — moved from
/// <c>Admins.Infrastructure.Persistence.Users.AuthAuditWriter</c> (task 8.5.1), unchanged.</summary>
internal sealed class AuthAuditWriter : IAuthAuditWriter
{
    private readonly ControlPlaneDbContext _db;

    public AuthAuditWriter(ControlPlaneDbContext db) => _db = db;

    public void Append(AuthAudit entry) => _db.AuthAudits.Add(entry);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
