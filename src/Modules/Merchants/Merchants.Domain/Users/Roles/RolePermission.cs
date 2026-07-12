using SharedKernel;

namespace Merchants.Domain.Users.Roles;

/// <summary>One permission key granted to a role — a standalone child row with a surrogate id, a unique
/// (RoleId, PermissionKey), and a FK to merch.Permissions so a role can never grant a key outside the
/// catalog. Created and removed only through the <see cref="Role"/> aggregate.</summary>
// ponytail: DUPLICATE of Admins.Domain.Roles.RolePermission — deliberate debt, do not refactor into a shared base.
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
