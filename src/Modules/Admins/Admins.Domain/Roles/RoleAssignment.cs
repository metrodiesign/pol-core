using SharedKernel;

namespace Admins.Domain.Roles;

/// <summary>Links an <see cref="User"/> to an <see cref="Role"/> (REQ-4). Standalone child row with a
/// surrogate id and a unique (PlatformUserId, RoleId); <see cref="AssignedByAdminId"/> records who granted it.
/// Mirrors <see cref="MerchantAccess"/> exactly.</summary>
public sealed class RoleAssignment : Entity<Guid>
{
    public Guid PlatformUserId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid AssignedByAdminId { get; private set; }
    public DateTime AssignedAt { get; private set; }

    private RoleAssignment() { }

    private RoleAssignment(Guid id, Guid adminAccountId, Guid roleId, Guid assignedByAdminId, DateTime assignedAt)
        : base(id)
    {
        PlatformUserId = adminAccountId;
        RoleId = roleId;
        AssignedByAdminId = assignedByAdminId;
        AssignedAt = assignedAt;
    }

    public static RoleAssignment Create(Guid adminAccountId, Guid roleId, Guid assignedByAdminId, DateTime assignedAt) =>
        new(Guid.NewGuid(), adminAccountId, roleId, assignedByAdminId, assignedAt);
}
