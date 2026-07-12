using Admins.Application.Permissions;
using Admins.Application.Roles;
using Admins.Domain.Permissions;
using Admins.Domain.Roles;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Admins.Infrastructure.Persistence.Roles;

/// <summary>Admin Role RBAC persistence over the shared admin data plane, bound by the host to the pol_admin
/// (RLS-bypass) connection — role/catalog tables are control-plane. Effective-permission resolution is a LINQ
/// union over the admin's ACTIVE roles (REQ-5.1, N1).</summary>
public sealed class RoleRepository : IRoleRepository
{
    private readonly PolDbContext _db;
    private readonly ILogger<RoleRepository> _logger;

    public RoleRepository(PolDbContext db, ILogger<RoleRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Add(Role role) => _db.Set<Role>().Add(role);
    public void Remove(Role role) => _db.Set<Role>().Remove(role);
    public void AddAssignment(RoleAssignment assignment) => _db.Set<RoleAssignment>().Add(assignment);
    public void RemoveAssignment(RoleAssignment assignment) => _db.Set<RoleAssignment>().Remove(assignment);

    public Task<Role?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<Role>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code, cancellationToken);

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<Role>().AnyAsync(r => r.Code == code, cancellationToken);

    public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<RoleAssignment>().CountAsync(a => a.RoleId == roleId, cancellationToken);

    public async Task<PagedResult<RoleListItem>> ListAsync(PagedQuery query, CancellationToken cancellationToken)
    {
        IQueryable<Role> src = _db.Set<Role>().AsNoTracking()
            .ApplySearch(query.Search)
            .ApplyFilters(query.Filters, _logger);

        long total = await src.LongCountAsync(cancellationToken);   // count after filter/search, before paging (REQ-2.5)

        // Offset computed in long so a huge page can never overflow int into a negative SQL OFFSET (REQ-2.6);
        // the Hosts parser already clamps page to the offset ceiling.
        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);

        // Materialize the page as ENTITIES first: ToListItem() and role.PermissionKeys are computed members EF
        // cannot translate in a server-side Select (the Product.Price hazard; D15).
        var roles = await src
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Include(r => r.Permissions)
            .ToListAsync(cancellationToken);

        // Preserve UserCount — losing it is a REQ-12.1 regression. Grouped assignment count for THIS page's roles.
        var ids = roles.Select(r => r.Id).ToList();
        var counts = await _db.Set<RoleAssignment>().AsNoTracking()
            .Where(a => ids.Contains(a.RoleId))
            .GroupBy(a => a.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        // Map client-side (entities are already materialized).
        var items = roles.Select(r => ToListItem(r, counts.GetValueOrDefault(r.Id))).ToList();
        return new PagedResult<RoleListItem>(items, query.Page, query.Limit, total);
    }

    public async Task<RoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var role = await _db.Set<Role>().Include(r => r.Permissions).AsNoTracking()
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
        return await _db.Set<Role>()
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<RoleAssignment>()
            .Where(a => a.PlatformUserId == adminId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<RoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<RoleAssignment>()
            .FirstOrDefaultAsync(a => a.PlatformUserId == adminId && a.RoleId == roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<RoleAssignment>().AnyAsync(a => a.PlatformUserId == adminId && a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken)
    {
        var keys = await _db.Set<Permission>().Select(p => p.Key).ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<PermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken)
    {
        var groups = await _db.Set<PermissionGroup>().AsNoTracking()
            .OrderBy(g => g.SortOrder)
            .Select(g => new PermissionGroupItem(g.Key, g.LabelTh))
            .ToListAsync(cancellationToken);
        var permissions = await _db.Set<Permission>().AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .Select(p => new PermissionItem(p.Key, p.LabelTh, p.GroupKey))
            .ToListAsync(cancellationToken);
        return new PermissionCatalogResult(groups, permissions);
    }

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var keys = await _db.Set<RoleAssignment>()
            .Where(a => a.PlatformUserId == adminId)
            .Join(_db.Set<Role>().Where(r => r.Status == RoleStatus.Active),
                  a => a.RoleId, r => r.Id, (a, r) => r.Id)
            .Join(_db.Set<RolePermission>(), roleId => roleId, p => p.RoleId, (roleId, p) => p.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListRoleCodesForAdminAsync(Guid adminId, CancellationToken cancellationToken)
    {
        // ALL assigned roles incl. Inactive (assignment truth, REQ-2.1) — no Active filter, unlike effective perms.
        return await _db.Set<RoleAssignment>()
            .Where(a => a.PlatformUserId == adminId)
            .Join(_db.Set<Role>(), a => a.RoleId, r => r.Id, (a, r) => r.Code)
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
    }

    private static RoleListItem ToListItem(Role role, int userCount) =>
        new(role.Code, role.Name, role.Description, role.Color, role.Status, [.. role.PermissionKeys], userCount);
}
