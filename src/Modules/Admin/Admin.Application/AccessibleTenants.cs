namespace Admin.Application;

/// <summary>
/// The set of tenants an admin may reach, resolved once per request (REQ-6). A Super is
/// <see cref="IsUnrestricted"/> (all tenants); a Scoped admin carries exactly its assigned set.
/// <see cref="Allows"/> is the single decision the scoped app-layer floor (REQ-7) calls.
/// </summary>
public readonly record struct AccessibleTenants(bool IsUnrestricted, IReadOnlySet<Guid> Tenants)
{
    private static readonly IReadOnlySet<Guid> None = new HashSet<Guid>();

    /// <summary>Unrestricted reach (a Super admin).</summary>
    public static AccessibleTenants All { get; } = new(true, None);

    /// <summary>Exactly the assigned set (a Scoped admin).</summary>
    public static AccessibleTenants Of(IReadOnlySet<Guid> tenants) => new(false, tenants);

    public bool Allows(Guid tenantId) => IsUnrestricted || Tenants.Contains(tenantId);
}
