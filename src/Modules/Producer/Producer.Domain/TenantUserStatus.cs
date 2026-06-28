namespace Producer.Domain;

/// <summary>Lifecycle of a producer-side <see cref="TenantUser"/> (REQ-1.2). A registrant starts
/// <see cref="PendingApproval"/>; an admin <see cref="Active"/>ates them onto a tenant or
/// <see cref="Rejected"/>s them; a rejected applicant may resubmit (back to <see cref="PendingApproval"/>);
/// an active user may later be <see cref="Suspended"/>. Stored as int.</summary>
public enum TenantUserStatus
{
    PendingApproval = 0,
    Active = 1,
    Rejected = 2,
    Suspended = 3,
}
