using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Iam.Application.Permissions;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iam.Infrastructure.Persistence.Roles;

/// <summary>Central IAM Role RBAC persistence (REQ-1/2/3/6), replacing the two duplicated
/// <c>Admins.Infrastructure.Persistence.Roles.RoleRepository</c> / <c>Merchants.Infrastructure...RoleRepository</c>
/// CRUD surfaces. Bound by the host to the pol_admin (RLS-bypass) keyed context — role/catalog tables are
/// control-plane. Every read applies <see cref="RoleVisibility"/> FIRST (REQ-3.6/3.9) — there is no
/// unscoped query left in this store (the old merchant catalog's wart: GetByCode/CodeExists/List/
/// GetListItem/GetRoleIdsByCodes filtered nothing).</summary>
public sealed class RoleStore : IRoleStore
{
    private readonly PolDbContext _db;
    private readonly ILogger<RoleStore> _logger;

    public RoleStore(PolDbContext db, ILogger<RoleStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Add(Role role) => _db.Set<Role>().Add(role);
    public void Remove(Role role) => _db.Set<Role>().Remove(role);

    public Task<Role?> GetByCodeAsync(RoleSideContext context, string code, CancellationToken cancellationToken) =>
        _db.Set<Role>().Include(r => r.Permissions)
            .Where(RoleVisibility.For(context.Scope, context.MerchantId))
            .FirstOrDefaultAsync(r => r.Code == code, cancellationToken);

    public async Task<bool> CodeExistsAsync(RoleSideContext context, string code, CancellationToken cancellationToken)
    {
        var visible = await _db.Set<Role>()
            .Where(RoleVisibility.For(context.Scope, context.MerchantId))
            .AnyAsync(r => r.Code == code, cancellationToken);
        if (visible)
            return true;

        // Only relevant when THIS context's own insert would also land in the shared NULL bucket (Platform,
        // or a shared Merchant role) — see the XML doc on IRoleStore.CodeExistsAsync (critique P2-5).
        if (context.MerchantId is null)
            return await _db.Set<Role>().AnyAsync(r => r.MerchantId == null && r.Code == code, cancellationToken);
        return false;
    }

    public async Task<PagedResult<RoleListItem>> ListAsync(
        RoleSideContext context, PagedQuery query, CancellationToken cancellationToken)
    {
        IQueryable<Role> src = _db.Set<Role>().AsNoTracking()
            .Where(RoleVisibility.For(context.Scope, context.MerchantId))
            .ApplySearch(query.Search)
            .ApplyFilters(query.Filters, _logger);

        long total = await src.LongCountAsync(cancellationToken);

        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);

        var roles = await src
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Include(r => r.Permissions)
            .ToListAsync(cancellationToken);

        var items = roles.Select(r => ToListItem(r, userCount: 0)).ToList(); // caller fills UserCount (IRoleAssignmentCounter)
        return new PagedResult<RoleListItem>(items, query.Page, query.Limit, total);
    }

    public async Task<RoleListItem?> GetListItemByCodeAsync(
        RoleSideContext context, string code, CancellationToken cancellationToken)
    {
        var role = await _db.Set<Role>().AsNoTracking().Include(r => r.Permissions)
            .Where(RoleVisibility.For(context.Scope, context.MerchantId))
            .FirstOrDefaultAsync(r => r.Code == code, cancellationToken);
        return role is null ? null : ToListItem(role, userCount: 0); // caller fills UserCount (IRoleAssignmentCounter)
    }

    public async Task<PermissionCatalogResult> ListCatalogAsync(Scope scope, CancellationToken cancellationToken)
    {
        var groups = await _db.Set<PermissionGroup>().AsNoTracking()
            .Where(g => g.Scope == scope)
            .OrderBy(g => g.SortOrder)
            .Select(g => new PermissionGroupItem(g.Key, g.LabelTh))
            .ToListAsync(cancellationToken);
        var permissions = await _db.Set<Permission>().AsNoTracking()
            .Join(_db.Set<PermissionGroup>(), p => p.GroupKey, g => g.Key, (p, g) => new { p, g.Scope })
            .Where(x => x.Scope == scope)
            .OrderBy(x => x.p.SortOrder)
            .Select(x => new PermissionItem(x.p.Key, x.p.LabelTh, x.p.GroupKey))
            .ToListAsync(cancellationToken);
        return new PermissionCatalogResult(groups, permissions);
    }

    private static RoleListItem ToListItem(Role role, int userCount) =>
        new(role.Id, role.Code, role.Name, role.Description, role.Color, role.Status,
            Shared: role.MerchantId is null, [.. role.PermissionKeys], userCount);
}
