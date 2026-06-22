namespace Identity.Domain;

/// <summary>Tenant Console roles (reference 2.3). Scope = the user's own tenant.</summary>
public enum TenantUserRole
{
    Viewer = 0,
    Finance = 1,
    TenantAdmin = 2,
}
