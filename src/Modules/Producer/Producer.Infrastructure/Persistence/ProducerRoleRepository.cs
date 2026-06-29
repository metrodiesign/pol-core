using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Producer.Application;
using Producer.Domain;

namespace Producer.Infrastructure.Persistence;

/// <summary>Producer Role RBAC persistence over the shared producer data plane, bound by the host to the pol_admin
/// (RLS-bypass) connection — role/catalog tables are control-plane. Effective-permission resolution is a LINQ union
/// over the producer's ACTIVE roles, scoped to the tenant the user was approved into (REQ-16.4/17.1).</summary>
// ponytail: DUPLICATE of Admin.Infrastructure.Persistence.AdminRoleRepository (+ tenant-scoped union) — deliberate debt, do not refactor into a shared base.
public sealed class ProducerRoleRepository : IProducerRoleRepository
{
    private readonly ProducerDbContext _db;

    public ProducerRoleRepository(ProducerDbContext db) => _db = db;

    public void Add(ProducerRole role) => _db.Set<ProducerRole>().Add(role);
    public void Remove(ProducerRole role) => _db.Set<ProducerRole>().Remove(role);
    public void AddAssignment(ProducerRoleAssignment assignment) => _db.Set<ProducerRoleAssignment>().Add(assignment);
    public void RemoveAssignment(ProducerRoleAssignment assignment) => _db.Set<ProducerRoleAssignment>().Remove(assignment);

    public Task<ProducerRole?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<ProducerRole>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code, cancellationToken);

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<ProducerRole>().AnyAsync(r => r.Code == code, cancellationToken);

    public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<ProducerRoleAssignment>().CountAsync(a => a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlyList<ProducerRoleListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await _db.Set<ProducerRole>().Include(r => r.Permissions).AsNoTracking().ToListAsync(cancellationToken);
        var counts = await _db.Set<ProducerRoleAssignment>()
            .GroupBy(a => a.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        return [.. roles.Select(r => ToListItem(r, counts.GetValueOrDefault(r.Id)))];
    }

    public async Task<ProducerRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var role = await _db.Set<ProducerRole>().Include(r => r.Permissions).AsNoTracking()
            .FirstOrDefaultAsync(r => r.Code == code, cancellationToken);
        if (role is null)
            return null;
        var count = await CountAssignmentsForRoleAsync(role.Id, cancellationToken);
        return ToListItem(role, count);
    }

    public async Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, Guid>();
        return await _db.Set<ProducerRole>()
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid tenantUserId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<ProducerRoleAssignment>()
            .Where(a => a.TenantUserId == tenantUserId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<ProducerRoleAssignment?> GetAssignmentAsync(Guid tenantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<ProducerRoleAssignment>()
            .FirstOrDefaultAsync(a => a.TenantUserId == tenantUserId && a.RoleId == roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid tenantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<ProducerRoleAssignment>().AnyAsync(a => a.TenantUserId == tenantUserId && a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken)
    {
        var keys = await _db.Set<ProducerPermission>().Select(p => p.Key).ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<ProducerPermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken)
    {
        var groups = await _db.Set<ProducerPermissionGroup>().AsNoTracking()
            .OrderBy(g => g.SortOrder)
            .Select(g => new ProducerPermissionGroupItem(g.Key, g.LabelTh))
            .ToListAsync(cancellationToken);
        var permissions = await _db.Set<ProducerPermission>().AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .Select(p => new ProducerPermissionItem(p.Key, p.LabelTh, p.GroupKey))
            .ToListAsync(cancellationToken);
        return new ProducerPermissionCatalogResult(groups, permissions);
    }

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
        Guid tenantUserId, Guid tenantId, CancellationToken cancellationToken)
    {
        // Union of keys over the user's assigned roles that are (a) scoped to the tenant they were approved into and
        // (b) Active. An Inactive role contributes nothing; zero active roles -> empty set (REQ-16.4).
        var keys = await _db.Set<ProducerRoleAssignment>()
            .Where(a => a.TenantUserId == tenantUserId && a.TenantId == tenantId)
            .Join(_db.Set<ProducerRole>().Where(r => r.Status == ProducerRoleStatus.Active),
                  a => a.RoleId, r => r.Id, (a, r) => r.Id)
            .Join(_db.Set<ProducerRolePermission>(), roleId => roleId, p => p.RoleId, (roleId, p) => p.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(
        Guid tenantUserId, Guid tenantId, CancellationToken cancellationToken) =>
        await _db.Set<ProducerRoleAssignment>()
            .Where(a => a.TenantUserId == tenantUserId && a.TenantId == tenantId)
            .Join(_db.Set<ProducerRole>().Where(r => r.Status == ProducerRoleStatus.Active),
                  a => a.RoleId, r => r.Id, (a, r) => r.Code)
            .Distinct()
            .ToListAsync(cancellationToken);

    private static ProducerRoleListItem ToListItem(ProducerRole role, int userCount) =>
        new(role.Code, role.Name, role.Description, role.Color, role.Status, [.. role.PermissionKeys], userCount);
}
