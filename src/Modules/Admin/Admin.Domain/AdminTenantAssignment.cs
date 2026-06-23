using SharedKernel;

namespace Admin.Domain;

/// <summary>An edge granting a Scoped <see cref="AdminAccount"/> reach over one tenant (REQ-4). Control-plane;
/// <see cref="TenantId"/> is a SOFT reference to a Tenant (no DB FK — Admin does not reference the Tenant
/// module; existence/active is validated via the host's <c>IAdminTenantDirectory</c> at assignment time).
/// Uniqueness on <c>(AdminAccountId, TenantId)</c> is enforced by the DB index; unassign is a hard delete.</summary>
public sealed class AdminTenantAssignment : Entity<Guid>
{
    public Guid AdminAccountId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssignedByAdminId { get; private set; }
    public DateTime AssignedAtUtc { get; private set; }

    private AdminTenantAssignment() { }

    private AdminTenantAssignment(Guid id, Guid adminAccountId, Guid tenantId, Guid assignedByAdminId, DateTime assignedAtUtc)
        : base(id)
    {
        AdminAccountId = adminAccountId;
        TenantId = tenantId;
        AssignedByAdminId = assignedByAdminId;
        AssignedAtUtc = assignedAtUtc;
    }

    public static AdminTenantAssignment Create(Guid adminAccountId, Guid tenantId, Guid assignedByAdminId, DateTime assignedAtUtc)
    {
        if (adminAccountId == Guid.Empty)
            throw new ArgumentException("AdminAccountId is required.", nameof(adminAccountId));
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        return new AdminTenantAssignment(Guid.NewGuid(), adminAccountId, tenantId, assignedByAdminId, assignedAtUtc);
    }
}
