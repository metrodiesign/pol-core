using Admin.Application;
using Admin.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Admin.Infrastructure.Persistence;

/// <summary>
/// EF-backed admin session store on the keyed pol_admin <see cref="ProducerDbContext"/>. Reads are AsNoTracking;
/// every status transition that can race (rotate/revoke/slide) is a single set-based <c>ExecuteUpdate</c> so two
/// concurrent requests cannot lost-update — the affected-row count of <see cref="TrySupersedeAsync"/> is the
/// single-winner flag for rotation (REQ-5.5).
/// </summary>
public sealed class AdminSessionStore : IAdminSessionStore
{
    private readonly ProducerDbContext _db;

    public AdminSessionStore(ProducerDbContext db) => _db = db;

    public Task<AdminSession?> FindByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
        _db.Set<AdminSession>().AsNoTracking().FirstOrDefaultAsync(s => s.TokenHash == tokenHash, cancellationToken);

    public async Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken cancellationToken)
    {
        // The family invariant is at most one Active session; if a bug ever yields more, treat it as "no single
        // active" so the immediate-predecessor (reuse) check fails closed rather than picking an arbitrary id.
        var activeIds = await _db.Set<AdminSession>().AsNoTracking()
            .Where(s => s.FamilyId == familyId && s.Status == AdminSessionStatus.Active)
            .Select(s => s.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        return activeIds.Count == 1 ? activeIds[0] : null;
    }

    public void Add(AdminSession session) => _db.Set<AdminSession>().Add(session);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);

    public async Task<bool> TrySupersedeAsync(Guid sessionId, Guid successorId, DateTime now, CancellationToken cancellationToken)
    {
        var affected = await _db.Set<AdminSession>()
            .Where(s => s.Id == sessionId && s.Status == AdminSessionStatus.Active)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.Status, AdminSessionStatus.Superseded)
                .SetProperty(s => s.SupersededAt, now)
                .SetProperty(s => s.SupersededBySessionId, successorId), cancellationToken);
        return affected > 0;
    }

    public Task SlideIdleAsync(Guid sessionId, DateTime idleExpiresAt, CancellationToken cancellationToken) =>
        _db.Set<AdminSession>()
            .Where(s => s.Id == sessionId && s.Status == AdminSessionStatus.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.IdleExpiresAt, idleExpiresAt), cancellationToken);

    public Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken) =>
        _db.Set<AdminSession>()
            .Where(s => s.FamilyId == familyId && s.Status != AdminSessionStatus.Revoked)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, AdminSessionStatus.Revoked), cancellationToken);

    public Task RevokeAllForAdminAsync(Guid adminAccountId, CancellationToken cancellationToken) =>
        _db.Set<AdminSession>()
            .Where(s => s.AdminAccountId == adminAccountId && s.Status != AdminSessionStatus.Revoked)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, AdminSessionStatus.Revoked), cancellationToken);

    // Every session carries an absolute expiry <= 8h out, so deleting past-absolute rows bounds the table for
    // expired AND revoked sessions alike (a revoked session is gone within its remaining absolute lifetime).
    public Task<int> PruneAsync(DateTime now, CancellationToken cancellationToken) =>
        _db.Set<AdminSession>().Where(s => s.AbsoluteExpiresAt < now).ExecuteDeleteAsync(cancellationToken);

    // admin-account-management REQ-4.1: newest first + id tiebreak, unpaged (prune bounds the set). AsNoTracking —
    // a read for the console.
    public async Task<IReadOnlyList<AdminSession>> ListByAdminAsync(Guid adminAccountId, CancellationToken cancellationToken) =>
        await _db.Set<AdminSession>().AsNoTracking()
            .Where(s => s.AdminAccountId == adminAccountId)
            .OrderByDescending(s => s.IssuedAt).ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

    // admin-account-management REQ-5: read one session (ownership check + FamilyId) before a family revoke.
    public Task<AdminSession?> FindByIdAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Set<AdminSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
}

/// <summary>Append-only writer for <c>AdminAuthAudits</c> (REQ-12.2) on the keyed pol_admin context.</summary>
public sealed class AdminAuthAuditWriter : IAdminAuthAuditWriter
{
    private readonly ProducerDbContext _db;

    public AdminAuthAuditWriter(ProducerDbContext db) => _db = db;

    public void Append(AdminAuthAudit entry) => _db.Set<AdminAuthAudit>().Add(entry);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => _db.SaveChangesAsync(cancellationToken);
}
