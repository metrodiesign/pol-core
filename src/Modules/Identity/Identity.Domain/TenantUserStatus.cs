namespace Identity.Domain;

/// <summary>Lifecycle of a tenant user. A new applicant is <see cref="PendingApproval"/> until an admin
/// binds it to a tenant + role (-> <see cref="Active"/>); <see cref="Suspended"/> revokes access.</summary>
public enum TenantUserStatus
{
    PendingApproval = 0,
    Active = 1,
    Suspended = 2,
}
