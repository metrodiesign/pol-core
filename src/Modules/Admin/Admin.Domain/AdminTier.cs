namespace Admin.Domain;

/// <summary>Admin privilege level (admin-actor-rename REQ-3). <see cref="Super"/> has unrestricted
/// cross-tenant control; <see cref="Scoped"/> reaches only the tenants assigned to it
/// (<see cref="AdminTenantAssignment"/>).</summary>
public enum AdminTier
{
    Scoped = 0,
    Super = 1,
}
