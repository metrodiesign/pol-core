using SharedKernel;

namespace Producer.Domain;

/// <summary>One permission key granted to a role — a standalone child row with a surrogate id, a unique
/// (RoleId, PermissionKey), and a FK to producer.ProducerPermissions so a role can never grant a key outside the
/// catalog (REQ-16.2). Created and removed only through the <see cref="ProducerRole"/> aggregate.</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminRolePermission — deliberate debt, do not refactor into a shared base.
public sealed class ProducerRolePermission : Entity<Guid>
{
    public Guid RoleId { get; private set; }
    public string PermissionKey { get; private set; } = default!;

    private ProducerRolePermission() { }

    internal ProducerRolePermission(Guid roleId, string permissionKey) : base(Guid.NewGuid())
    {
        RoleId = roleId;
        PermissionKey = permissionKey;
    }
}
