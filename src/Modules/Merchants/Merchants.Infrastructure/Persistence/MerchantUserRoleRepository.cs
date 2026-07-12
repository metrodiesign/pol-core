using BuildingBlocks.Infrastructure.Persistence;
using Merchants.Application;
using Merchants.Domain;
using Microsoft.EntityFrameworkCore;

namespace Merchants.Infrastructure.Persistence;

/// <summary>Merchant-User Role RBAC persistence over the shared data plane, bound by the host to the pol_admin
/// (RLS-bypass) connection — role/catalog tables are control-plane. Effective-permission resolution is a LINQ union
/// over the user's ACTIVE roles, scoped to the merchant the user was approved into (REQ-16.4/17.1).</summary>
// ponytail: DUPLICATE of Admins.Infrastructure.Persistence.AdminRoleRepository (+ merchant-scoped union) — deliberate debt, do not refactor into a shared base.
public sealed class MerchantUserRoleRepository : IMerchantUserRoleRepository
{
    private readonly PolDbContext _db;

    public MerchantUserRoleRepository(PolDbContext db) => _db = db;

    public void Add(MerchantUserRoleDefinition role) => _db.Set<MerchantUserRoleDefinition>().Add(role);
    public void Remove(MerchantUserRoleDefinition role) => _db.Set<MerchantUserRoleDefinition>().Remove(role);
    public void AddAssignment(MerchantUserRoleAssignment assignment) => _db.Set<MerchantUserRoleAssignment>().Add(assignment);
    public void RemoveAssignment(MerchantUserRoleAssignment assignment) => _db.Set<MerchantUserRoleAssignment>().Remove(assignment);

    public Task<MerchantUserRoleDefinition?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<MerchantUserRoleDefinition>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Code == code, cancellationToken);

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        _db.Set<MerchantUserRoleDefinition>().AnyAsync(r => r.Code == code, cancellationToken);

    public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<MerchantUserRoleAssignment>().CountAsync(a => a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlyList<MerchantUserRoleListItem>> ListAsync(CancellationToken cancellationToken)
    {
        var roles = await _db.Set<MerchantUserRoleDefinition>().Include(r => r.Permissions).AsNoTracking().ToListAsync(cancellationToken);
        var counts = await _db.Set<MerchantUserRoleAssignment>()
            .GroupBy(a => a.RoleId)
            .Select(g => new { RoleId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RoleId, x => x.Count, cancellationToken);

        return [.. roles.Select(r => ToListItem(r, counts.GetValueOrDefault(r.Id)))];
    }

    public async Task<MerchantUserRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken cancellationToken)
    {
        var role = await _db.Set<MerchantUserRoleDefinition>().Include(r => r.Permissions).AsNoTracking()
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
        return await _db.Set<MerchantUserRoleDefinition>()
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken cancellationToken)
    {
        var ids = await _db.Set<MerchantUserRoleAssignment>()
            .Where(a => a.MerchantUserId == merchantUserId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<MerchantUserRoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<MerchantUserRoleAssignment>()
            .FirstOrDefaultAsync(a => a.MerchantUserId == merchantUserId && a.RoleId == roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken cancellationToken) =>
        _db.Set<MerchantUserRoleAssignment>().AnyAsync(a => a.MerchantUserId == merchantUserId && a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken cancellationToken)
    {
        var keys = await _db.Set<MerchantUserPermission>().Select(p => p.Key).ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<MerchantUserPermissionCatalogResult> ListCatalogAsync(CancellationToken cancellationToken)
    {
        var groups = await _db.Set<MerchantUserPermissionGroup>().AsNoTracking()
            .OrderBy(g => g.SortOrder)
            .Select(g => new MerchantUserPermissionGroupItem(g.Key, g.LabelTh))
            .ToListAsync(cancellationToken);
        var permissions = await _db.Set<MerchantUserPermission>().AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .Select(p => new MerchantUserPermissionItem(p.Key, p.LabelTh, p.GroupKey))
            .ToListAsync(cancellationToken);
        return new MerchantUserPermissionCatalogResult(groups, permissions);
    }

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken)
    {
        // Union of keys over the user's assigned roles that are (a) scoped to the merchant they were approved into and
        // (b) Active. An Inactive role contributes nothing; zero active roles -> empty set (REQ-16.4).
        var keys = await _db.Set<MerchantUserRoleAssignment>()
            .Where(a => a.MerchantUserId == merchantUserId && a.MerchantId == merchantId)
            .Join(_db.Set<MerchantUserRoleDefinition>().Where(r => r.Status == MerchantUserRoleStatus.Active),
                  a => a.RoleId, r => r.Id, (a, r) => r.Id)
            .Join(_db.Set<MerchantUserRolePermission>(), roleId => roleId, p => p.RoleId, (roleId, p) => p.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(
        Guid merchantUserId, Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<MerchantUserRoleAssignment>()
            .Where(a => a.MerchantUserId == merchantUserId && a.MerchantId == merchantId)
            .Join(_db.Set<MerchantUserRoleDefinition>().Where(r => r.Status == MerchantUserRoleStatus.Active),
                  a => a.RoleId, r => r.Id, (a, r) => r.Code)
            .Distinct()
            .ToListAsync(cancellationToken);

    private static MerchantUserRoleListItem ToListItem(MerchantUserRoleDefinition role, int userCount) =>
        new(role.Code, role.Name, role.Description, role.Color, role.Status, [.. role.PermissionKeys], userCount);
}
