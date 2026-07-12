using Admins.Application.Roles;
using Admins.Domain.Roles;
using BuildingBlocks.Infrastructure.Persistence;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;

namespace Admins.Infrastructure.Persistence.Roles;

/// <summary>
/// Admin-side role assignment + resolution persistence, bound by the host to the pol_admin (RLS-bypass)
/// connection — a "resolution repository" (design.md confinement carve-out): it queries the central
/// <c>iam.Roles</c>/<c>iam.RolePermissions</c> tables directly through the published <c>Iam.Domain</c> types,
/// always through <see cref="RoleVisibility"/> so a lookup can never resolve a Merchant-scope role
/// (REQ-3.4/3.9). The assignment edge (<c>admin.RoleAssignments</c>) is this module's own.
/// </summary>
public sealed class RoleRepository : IRoleRepository
{
    private readonly PolDbContext _db;

    public RoleRepository(PolDbContext db) => _db = db;

    public void AddAssignment(RoleAssignment assignment) => _db.Set<RoleAssignment>().Add(assignment);
    public void RemoveAssignment(RoleAssignment assignment) => _db.Set<RoleAssignment>().Remove(assignment);

    public async Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
            return new Dictionary<string, Guid>();
        return await _db.Set<Role>()
            .Where(RoleVisibility.For(Scope.Platform, null))
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

    public async Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid adminId, CancellationToken cancellationToken)
    {
        var keys = await _db.Set<RoleAssignment>()
            .Where(a => a.PlatformUserId == adminId)
            .Join(
                _db.Set<Role>().Where(RoleVisibility.For(Scope.Platform, null)).Where(r => r.Status == RoleStatus.Active),
                a => a.RoleId, r => r.Id, (a, r) => r.Id)
            .Join(_db.Set<RolePermission>(), roleId => roleId, p => p.RoleId, (roleId, p) => p.PermissionKey)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListRoleCodesForAdminAsync(Guid adminId, CancellationToken cancellationToken) =>
        // ALL assigned roles incl. Inactive (assignment truth) — no Active filter, unlike effective perms.
        await _db.Set<RoleAssignment>()
            .Where(a => a.PlatformUserId == adminId)
            .Join(_db.Set<Role>(), a => a.RoleId, r => r.Id, (a, r) => r.Code)
            .OrderBy(c => c)
            .ToListAsync(cancellationToken);
}
