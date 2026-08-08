using Admins.Application.Roles;
using Admins.Domain.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// Admin-side role assignment + resolution persistence over <see cref="ControlPlaneDbContext"/> — a
/// "resolution repository" (design.md confinement carve-out): it queries the central <c>iam.Roles</c>/
/// <c>iam.RolePermissions</c> tables directly through the published <c>Iam.Domain</c> types, always through
/// <see cref="RoleVisibility"/> so a lookup can never resolve a Merchant-scope role (REQ-3.4/3.9). The
/// assignment edge (<c>admin.RoleAssignments</c>) is this module's own. Moved from
/// <c>Admins.Infrastructure.Persistence.Roles.RoleRepository</c> (task 8.5.1), unchanged.
/// </summary>
internal sealed class RoleRepository : IRoleRepository
{
    private readonly ControlPlaneDbContext _db;

    public RoleRepository(ControlPlaneDbContext db) => _db = db;

    public void AddAssignment(RoleAssignment assignment) => _db.RoleAssignments.Add(assignment);
    public void RemoveAssignment(RoleAssignment assignment) => _db.RoleAssignments.Remove(assignment);

    public async Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, Guid>();
        return await _db.Roles
            .Where(RoleVisibility.For(Scope.Platform, null))
            .Where(r => codes.Contains(r.Code))
            .ToDictionaryAsync(r => r.Code, r => r.Id, cancellationToken);
    }

    public async Task<IReadOnlySet<Guid>> ListRoleIdsForAdminAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var ids = await _db.RoleAssignments
            .Where(a => a.AdminUserId == adminId)
            .Select(a => a.RoleId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    public Task<RoleAssignment?> GetAssignmentAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken) =>
        _db.RoleAssignments
            .FirstOrDefaultAsync(a => a.AdminUserId == adminId && a.RoleId == roleId, cancellationToken);

    public Task<bool> AssignmentExistsAsync(Guid adminId, Guid roleId, CancellationToken cancellationToken) =>
        _db.RoleAssignments.AnyAsync(a => a.AdminUserId == adminId && a.RoleId == roleId, cancellationToken);

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var keys = await _db.RoleAssignments
            .Where(a => a.AdminUserId == adminId)
            .Join(
                _db.Roles.Where(RoleVisibility.For(Scope.Platform, null)).Where(r => r.Status == RoleStatus.Active),
                a => a.RoleId, r => r.Id, (a, r) => r.Id)
            .Join(_db.RolePermissions, roleId => roleId, p => p.RoleId, (roleId, p) => p.PermissionKey)
            .Join(_db.Permissions.Where(p => p.Status == PermissionStatus.Active), key => key, p => p.Key,
                (key, p) => new { key, p.GroupKey })
            .Join(_db.PermissionGroups.Where(g => g.Status == PermissionStatus.Active), x => x.GroupKey, g => g.Key,
                (x, g) => x.key)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListRoleCodesForAdminAsync(Guid adminId, CancellationToken cancellationToken) =>
        // ALL assigned roles incl. Inactive (assignment truth) — no Active filter, unlike effective perms.
        await _db.RoleAssignments
            .Where(a => a.AdminUserId == adminId)
            .Join(_db.Roles, a => a.RoleId, r => r.Id, (a, r) => r.Code)
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
}
