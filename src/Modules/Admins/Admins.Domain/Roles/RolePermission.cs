using SharedKernel;

namespace Admins.Domain.Roles;

/// <summary>One permission key granted to a role — a standalone child row (mirrors <see cref="MerchantAccess"/>)
/// with a surrogate id, a unique (RoleId, PermissionKey), and a FK to admin.Keys so a role can never
/// grant a key outside the catalog (REQ-3.1/3.2). Created and removed only through the <see cref="Role"/>
/// aggregate.</summary>
public sealed class RolePermission : Entity<Guid>
{
    public Guid RoleId { get; private set; }
    public string PermissionKey { get; private set; } = default!;

    private RolePermission() { }

    internal RolePermission(Guid roleId, string permissionKey) : base(Guid.NewGuid())
    {
        RoleId = roleId;
        PermissionKey = permissionKey;
    }
}
