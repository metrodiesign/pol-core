using SharedKernel;

namespace Admin.Domain;

/// <summary>Links an <see cref="AdminAccount"/> to an <see cref="AdminRole"/> (REQ-4). Standalone child row with a
/// surrogate id and a unique (AdminAccountId, RoleId); <see cref="AssignedByAdminId"/> records who granted it.
/// Mirrors <see cref="AdminTenantAssignment"/> exactly.</summary>
public sealed class AdminRoleAssignment : Entity<Guid>
{
    public Guid AdminAccountId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid AssignedByAdminId { get; private set; }
    public DateTime AssignedAt { get; private set; }

    private AdminRoleAssignment() { }

    private AdminRoleAssignment(Guid id, Guid adminAccountId, Guid roleId, Guid assignedByAdminId, DateTime assignedAt)
        : base(id)
    {
        AdminAccountId = adminAccountId;
        RoleId = roleId;
        AssignedByAdminId = assignedByAdminId;
        AssignedAt = assignedAt;
    }

    public static AdminRoleAssignment Create(Guid adminAccountId, Guid roleId, Guid assignedByAdminId, DateTime assignedAt) =>
        new(Guid.NewGuid(), adminAccountId, roleId, assignedByAdminId, assignedAt);
}
