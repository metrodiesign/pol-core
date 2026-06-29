using SharedKernel;

namespace Producer.Domain;

/// <summary>An edge binding a <see cref="ProducerAccount"/> to the one tenant it acts for (REQ-6). Control-plane;
/// <see cref="TenantId"/> is a SOFT reference to a Tenant (no DB FK — Producer does not reference the Tenant
/// module; existence/active is validated at the host before approval). Mirrors
/// <see cref="Admin.Domain.AdminTenantAssignment"/>, BUT a producer acts for exactly one tenant: uniqueness is on
/// <see cref="ProducerAccountId"/> alone (DB index), so a second tenant for the same account is rejected.</summary>
public sealed class ProducerTenantAssignment : Entity<Guid>
{
    public Guid ProducerAccountId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid AssignedByAdminId { get; private set; }
    public DateTime AssignedAt { get; private set; }

    private ProducerTenantAssignment() { }

    private ProducerTenantAssignment(Guid id, Guid producerAccountId, Guid tenantId, Guid assignedByAdminId, DateTime assignedAt)
        : base(id)
    {
        ProducerAccountId = producerAccountId;
        TenantId = tenantId;
        AssignedByAdminId = assignedByAdminId;
        AssignedAt = assignedAt;
    }

    public static ProducerTenantAssignment Create(Guid producerAccountId, Guid tenantId, Guid assignedByAdminId, DateTime assignedAt)
    {
        if (producerAccountId == Guid.Empty)
            throw new ArgumentException("ProducerAccountId is required.", nameof(producerAccountId));
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required.", nameof(tenantId));
        return new ProducerTenantAssignment(Guid.NewGuid(), producerAccountId, tenantId, assignedByAdminId, assignedAt);
    }
}
