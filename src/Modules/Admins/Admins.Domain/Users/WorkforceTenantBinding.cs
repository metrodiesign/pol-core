using SharedKernel;

namespace Admins.Domain.Users;

/// <summary>The immutable workforce tenant assigned to this Admin Console database.</summary>
public sealed class WorkforceTenantBinding : Entity<byte>
{
    public const byte SingletonId = 1;

    public Guid TenantId { get; private set; }

    private WorkforceTenantBinding() { }

    private WorkforceTenantBinding(Guid tenantId) : base(SingletonId) => TenantId = tenantId;

    public static WorkforceTenantBinding Create(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Workforce tenant ID cannot be empty.", nameof(tenantId));
        return new WorkforceTenantBinding(tenantId);
    }
}
