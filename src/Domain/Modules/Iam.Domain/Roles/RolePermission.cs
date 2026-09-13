using SharedKernel;

namespace Iam.Domain.Roles;

/// <summary>One permission key granted to a role — a standalone child row with a surrogate id, a unique
/// (RoleId, PermissionKey), and a FK to <c>iam.Permissions</c> so a role can never grant a key outside the
/// catalog (REQ-2.6). Created and removed only through the <see cref="Role"/> aggregate.</summary>
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
