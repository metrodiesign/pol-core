using SharedKernel;

namespace Admin.Domain;

/// <summary>One permission key granted to a role — a standalone child row (mirrors <see cref="PlatformMerchantAccess"/>)
/// with a surrogate id, a unique (RoleId, PermissionKey), and a FK to admin.AdminPermissions so a role can never
/// grant a key outside the catalog (REQ-3.1/3.2). Created and removed only through the <see cref="AdminRole"/>
/// aggregate.</summary>
public sealed class AdminRolePermission : Entity<Guid>
{
    public Guid RoleId { get; private set; }
    public string PermissionKey { get; private set; } = default!;

    private AdminRolePermission() { }

    internal AdminRolePermission(Guid roleId, string permissionKey) : base(Guid.NewGuid())
    {
        RoleId = roleId;
        PermissionKey = permissionKey;
    }
}
