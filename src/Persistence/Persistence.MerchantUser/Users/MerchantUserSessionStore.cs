using BuildingBlocks.Application;
using Merchants.Application.Users;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantUser.Users;

/// <summary>
/// rls-to-query-filter task 8.5.2 mirror of <c>Merchants.Infrastructure.Persistence.Users.SessionStore</c>
/// onto <see cref="MerchantUserDbContext"/>. <c>merch.Sessions</c> carries no query filter in this context
/// (task 2 — only <c>merch.Users</c>/<c>RoleAssignments</c> are merchant-filtered), so every read/write here
/// behaves identically to the <c>PolDbContext</c> original; only the connection changed. Bypass-primitive port
/// (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>) — named in the Architecture.Tests bypass-primitive allowlist.
/// </summary>
// ponytail: DUPLICATE of Persistence.ControlPlane.Admins.SessionStore / the old Merchants.Infrastructure
// SessionStore — deliberate debt (matches the original's own note), do not refactor into a shared base.
internal sealed class MerchantUserSessionStore : ISessionStore
{
    private readonly MerchantUserDbContext _db;
    private readonly ISecurityTelemetry _telemetry;

    public MerchantUserSessionStore(MerchantUserDbContext db, ISecurityTelemetry telemetry)
    {
        _db = db;
        _telemetry = telemetry;
    }

    public Task<Session?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public async Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken cancellationToken)
    {
        // The family invariant is at most one Active session; if a bug ever yields more, treat it as "no
        // single active" so the immediate-predecessor (reuse) check fails closed rather than picking an
        // arbitrary id.
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
                DenialCategory.PortCardinalityAnomaly, "merchant-user", ActorId: null, TargetMerchant: null,
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

    public Task RevokeAllForUserAsync(Guid merchantUserId, CancellationToken cancellationToken) =>
        _db.Sessions
            .Where(s => s.MerchantUserId == merchantUserId && s.Status != SessionStatus.Revoked)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, SessionStatus.Revoked), cancellationToken);

    // Every session carries an absolute expiry <= 8h out, so deleting past-absolute rows bounds the table for
    // expired AND revoked sessions alike (a revoked session is gone within its remaining absolute lifetime).
    public Task<int> PruneAsync(DateTime now, CancellationToken cancellationToken) =>
        _db.Sessions.Where(s => s.AbsoluteExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>Append-only writer for <c>merch.AuthAudits</c> on <see cref="MerchantUserDbContext"/>.</summary>
// ponytail: DUPLICATE of Persistence.ControlPlane.Admins.AuthAuditWriter — deliberate debt, do not refactor
// into a shared base.
internal sealed class MerchantUserAuthAuditWriter : IAuthAuditWriter
{
    private readonly MerchantUserDbContext _db;
    public MerchantUserAuthAuditWriter(MerchantUserDbContext db) => _db = db;

    public void Append(AuthAudit entry) => _db.AuthAudits.Add(entry);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
