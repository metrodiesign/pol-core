using SharedKernel;

namespace Merchants.Domain;

/// <summary>One permission key granted to a role — a standalone child row with a surrogate id, a unique
/// (RoleId, PermissionKey), and a FK to merch.MerchantUserPermissions so a role can never grant a key outside the
/// catalog. Created and removed only through the <see cref="MerchantUserRoleDefinition"/> aggregate.</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminRolePermission — deliberate debt, do not refactor into a shared base.
public sealed class MerchantUserRolePermission : Entity<Guid>
{
    public Guid RoleId { get; private set; }
    public string PermissionKey { get; private set; } = default!;

    private MerchantUserRolePermission() { }

    internal MerchantUserRolePermission(Guid roleId, string permissionKey) : base(Guid.NewGuid())
    {
        RoleId = roleId;
        PermissionKey = permissionKey;
    }
}
