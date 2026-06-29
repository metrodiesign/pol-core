using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Producer.Application;
using Producer.Domain;

namespace Producer.Infrastructure.Persistence;

/// <summary>
/// EF-backed producer session store on the keyed pol_admin <see cref="ProducerDbContext"/>. Reads are AsNoTracking;
/// every status transition that can race (rotate/revoke/slide) is a single set-based <c>ExecuteUpdate</c> so two
/// concurrent requests cannot lost-update — the affected-row count of <see cref="TrySupersedeAsync"/> is the
/// single-winner flag for rotation (REQ-11.5).
/// </summary>
// ponytail: DUPLICATE of Admin.Infrastructure.Persistence.AdminSessionStore (RevokeAllForAdmin -> RevokeAllForUser) — deliberate debt, do not refactor into a shared base.
public sealed class ProducerSessionStore : IProducerSessionStore
{
    private readonly ProducerDbContext _db;

    public ProducerSessionStore(ProducerDbContext db) => _db = db;

    public Task<ProducerSession?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        _db.Set<ProducerSession>().AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public async Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken cancellationToken)
    {
        // The family invariant is at most one Active session; if a bug ever yields more, treat it as "no single
        // active" so the immediate-predecessor (reuse) check fails closed rather than picking an arbitrary id.
        var activeIds = await _db.Set<ProducerSession>().AsNoTracking()
            .Where(s => s.FamilyId == familyId && s.Status == ProducerSessionStatus.Active)
            .Select(s => s.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        return activeIds.Count == 1 ? activeIds[0] : null;
    }

    public void Add(ProducerSession session) => _db.Set<ProducerSession>().Add(session);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public async Task<bool> TrySupersedeAsync(Guid sessionId, Guid successorId, DateTime now, CancellationToken cancellationToken)
    {
        var affected = await _db.Set<ProducerSession>()
            .Where(s => s.Id == sessionId && s.Status == ProducerSessionStatus.Active)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, ProducerSessionStatus.Superseded)
                .SetProperty(s => s.SupersededAt, now)
                .SetProperty(s => s.SupersededBySessionId, successorId), cancellationToken);
        return affected > 0;
    }

    public Task SlideIdleAsync(Guid sessionId, DateTime idleExpiresAt, CancellationToken cancellationToken) =>
        _db.Set<ProducerSession>()
            .Where(s => s.Id == sessionId && s.Status == ProducerSessionStatus.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.IdleExpiresAt, idleExpiresAt), cancellationToken);

    public Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        _db.Set<ProducerSession>()
            .Where(s => s.FamilyId == familyId && s.Status != ProducerSessionStatus.Revoked)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, ProducerSessionStatus.Revoked), cancellationToken);

    public Task RevokeAllForUserAsync(Guid tenantUserId, CancellationToken cancellationToken) =>
        _db.Set<ProducerSession>()
            .Where(s => s.ProducerAccountId == tenantUserId && s.Status != ProducerSessionStatus.Revoked)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, ProducerSessionStatus.Revoked), cancellationToken);

    // Every session carries an absolute expiry <= 8h out, so deleting past-absolute rows bounds the table for
    // expired AND revoked sessions alike (a revoked session is gone within its remaining absolute lifetime).
    public Task<int> PruneAsync(DateTime now, CancellationToken cancellationToken) =>
        _db.Set<ProducerSession>().Where(s => s.AbsoluteExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>Append-only writer for <c>ProducerAuthAudits</c> (REQ-12/21) on the keyed pol_admin context.</summary>
// ponytail: DUPLICATE of Admin.Infrastructure.Persistence.AdminAuthAuditWriter — deliberate debt, do not refactor into a shared base.
public sealed class ProducerAuthAuditWriter : IProducerAuthAuditWriter
{
    private readonly ProducerDbContext _db;

    public ProducerAuthAuditWriter(ProducerDbContext db) => _db = db;

    public void Append(ProducerAuthAudit entry) => _db.Set<ProducerAuthAudit>().Add(entry);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
