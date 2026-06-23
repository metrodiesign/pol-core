using SharedKernel;

namespace Identity.Domain;

/// <summary>Non-secret profile captured at registration (one per <see cref="TenantUser"/>).</summary>
public sealed class TenantUserProfile : Entity<Guid>
{
    public Guid TenantUserId { get; private set; }

    public string DisplayName { get; private set; } = default!;

    private TenantUserProfile() { }

    private TenantUserProfile(Guid id, Guid tenantUserId, string displayName) : base(id)
    {
        TenantUserId = tenantUserId;
        DisplayName = displayName;
    }

    public static TenantUserProfile Create(Guid tenantUserId, string displayName)
    {
        if (tenantUserId == Guid.Empty)
            throw new ArgumentException("TenantUserId is required.", nameof(tenantUserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        return new TenantUserProfile(Guid.NewGuid(), tenantUserId, displayName.Trim());
    }
}
