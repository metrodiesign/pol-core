using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Iam;

/// <summary>IAM-owned resolution repository for effective Account permissions. It is the only adapter that
/// joins Roles, RolePermissions, Permissions, and PermissionGroups for identity authorization; consumers receive
/// keys only and cannot query the IAM aggregate directly.</summary>
internal interface IIdentityAuthorizationRoleReader
{
    Task<IReadOnlySet<string>> ResolveEffectivePermissionsAsync(
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken);
}

internal sealed class IdentityAuthorizationRoleReader(ControlPlaneDbContext db)
    : IIdentityAuthorizationRoleReader
{
    public async Task<IReadOnlySet<string>> ResolveEffectivePermissionsAsync(
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var keys = await db.RolePermissions.AsNoTracking()
            .Where(permission => roleIds.Contains(permission.RoleId))
            .Join(db.Roles.AsNoTracking().Where(role => role.Status == RoleStatus.Active),
                permission => permission.RoleId,
                role => role.Id,
                (permission, _) => permission.PermissionKey)
            .Join(db.Permissions.AsNoTracking().Where(permission => permission.Status == PermissionStatus.Active),
                key => key,
                permission => permission.Key,
                (key, permission) => new { key, permission.GroupKey })
            .Join(db.PermissionGroups.AsNoTracking().Where(group => group.Status == PermissionStatus.Active),
                value => value.GroupKey,
                group => group.Key,
                (value, _) => value.key)
            .Distinct()
            .ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }
}
