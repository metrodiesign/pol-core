using Admin.Application;
using Admin.Domain;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Admin.Infrastructure.Persistence;

/// <summary>Admin Role RBAC persistence over the shared producer data plane, bound by the host to the pol_admin
/// (RLS-bypass) connection — role/catalog tables are control-plane. Effective-permission resolution is a LINQ
/// union over the admin's ACTIVE roles (REQ-5.1, N1).</summary>
public sealed class AdminRoleRepository : IAdminRoleRepository
{
    private readonly ProducerDbContext _db;

    public AdminRoleRepository(ProducerDbContext db) => _db = db;

    public void Add(AdminRole role) => _db.Set<AdminRole>().Add(role);
    public void Remove(AdminRole role) => _db.Set<AdminRole>().Remove(role);
    public void AddAssignment(AdminRoleAssignment assignment) => _db.Set<AdminRoleAssignment>().Add(assignment);
    public void RemoveAssignment(AdminRoleAssignment assignment) => _db.Set<AdminRoleAssignment>().Remove(assignment);

    public Task<AdminRole?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<AdminRole>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code, cancellationToken);

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<AdminRole>().AnyAsync(r => r.Code == code, cancellationToken);

    public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<AdminRoleAssignment>().CountAsync(a => a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlyList<AdminRoleListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await _db.Set<AdminRole>().Include(r => r.Permissions).AsNoTracking().ToListAsync(cancellationToken);
        var counts = await _db.Set<AdminRoleAssignment>()
            .GroupBy(a => a.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        return [.. roles.Select(r => ToListItem(r, counts.GetValueOrDefault(r.Id)))];
    }

    public async Task<AdminRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var role = await _db.Set<AdminRole>().Include(r => r.Permissions).AsNoTracking()
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
        return await _db.Set<AdminRole>()
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<AdminRoleAssignment>()
            .Where(a => a.AdminAccountId == adminId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<AdminRoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<AdminRoleAssignment>()
            .FirstOrDefaultAsync(a => a.AdminAccountId == adminId && a.RoleId == roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<AdminRoleAssignment>().AnyAsync(a => a.AdminAccountId == adminId && a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken)
    {
        var keys = await _db.Set<AdminPermission>().Select(p => p.Key).ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<PermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken)
    {
        var groups = await _db.Set<AdminPermissionGroup>().AsNoTracking()
            .OrderBy(g => g.SortOrder)
            .Select(g => new PermissionGroupItem(g.Key, g.LabelTh))
            .ToListAsync(cancellationToken);
        var permissions = await _db.Set<AdminPermission>().AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .Select(p => new PermissionItem(p.Key, p.LabelTh, p.GroupKey))
            .ToListAsync(cancellationToken);
        return new PermissionCatalogResult(groups, permissions);
    }

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var keys = await _db.Set<AdminRoleAssignment>()
            .Where(a => a.AdminAccountId == adminId)
            .Join(_db.Set<AdminRole>().Where(r => r.Status == AdminRoleStatus.Active),
                  a => a.RoleId, r => r.Id, (a, r) => r.Id)
            .Join(_db.Set<AdminRolePermission>(), roleId => roleId, p => p.RoleId, (roleId, p) => p.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    private static AdminRoleListItem ToListItem(AdminRole role, int userCount) =>
        new(role.Code, role.Name, role.Description, role.Color, role.Status, [.. role.PermissionKeys], userCount);
}
