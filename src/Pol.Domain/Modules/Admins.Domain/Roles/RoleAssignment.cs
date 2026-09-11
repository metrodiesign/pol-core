using SharedKernel;

namespace Admins.Domain.Roles;

/// <summary>Links an <see cref="User"/> to an <see cref="Role"/> (REQ-4). Standalone child row with a
/// surrogate id and a unique (AdminUserId, RoleId); <see cref="AssignedById"/> records who granted it.
/// Mirrors <see cref="MerchantAccess"/> exactly.</summary>
public sealed class RoleAssignment : Entity<Guid>
{
    public Guid AdminUserId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid AssignedById { get; private set; }
    public DateTime AssignedAt { get; private set; }

    private RoleAssignment() { }

    private RoleAssignment(Guid id, Guid adminAccountId, Guid roleId, Guid assignedById, DateTime assignedAt)
        : base(id)
    {
        AdminUserId = adminAccountId;
        RoleId = roleId;
        AssignedById = assignedById;
        AssignedAt = assignedAt;
    }

    public static RoleAssignment Create(Guid adminAccountId, Guid roleId, Guid assignedById, DateTime assignedAt) =>
        new(Guid.NewGuid(), adminAccountId, roleId, assignedById, assignedAt);
}
