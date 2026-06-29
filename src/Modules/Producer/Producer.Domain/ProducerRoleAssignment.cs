using SharedKernel;

namespace Producer.Domain;

/// <summary>Links a <see cref="TenantUser"/> to a <see cref="ProducerRole"/> within the tenant it was approved into
/// (REQ-16.3) — the F1 home of a producer's role(s), which are NOT a column on <see cref="TenantUser"/>. Standalone
/// child row with a surrogate id and a unique (TenantUserId, RoleId); <see cref="TenantId"/> scopes the grant to the
/// approved tenant and <see cref="AssignedByAdminId"/> records the approving admin. There is no per-assignment status
/// column: the effective-permission union keys on the ROLE's status (REQ-16.4), mirroring
/// <see cref="Admin.Domain.AdminRoleAssignment"/>.</summary>
// ponytail: DUPLICATE of Admin.Domain.AdminRoleAssignment (+ TenantId/TenantUserId rename) — deliberate debt, do not refactor into a shared base.
public sealed class ProducerRoleAssignment : Entity<Guid>
{
    public Guid TenantUserId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssignedByAdminId { get; private set; }
    public DateTime AssignedAt { get; private set; }

    private ProducerRoleAssignment() { }

    private ProducerRoleAssignment(Guid id, Guid tenantUserId, Guid roleId, Guid tenantId, Guid assignedByAdminId,
        DateTime assignedAt) : base(id)
    {
        TenantUserId = tenantUserId;
        RoleId = roleId;
        TenantId = tenantId;
        AssignedByAdminId = assignedByAdminId;
        AssignedAt = assignedAt;
    }

    public static ProducerRoleAssignment Create(Guid tenantUserId, Guid roleId, Guid tenantId, Guid assignedByAdminId,
        DateTime assignedAt)
    {
        if (tenantUserId == Guid.Empty)
            throw new ArgumentException("TenantUserId is required.", nameof(tenantUserId));
        if (roleId == Guid.Empty)
            throw new ArgumentException("RoleId is required.", nameof(roleId));
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        return new ProducerRoleAssignment(Guid.NewGuid(), tenantUserId, roleId, tenantId, assignedByAdminId, assignedAt);
    }
}
