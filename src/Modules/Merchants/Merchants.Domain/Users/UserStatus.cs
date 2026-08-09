namespace Merchants.Domain.Users;

/// <summary>Lifecycle of a <see cref="User"/>. A registrant starts
/// <see cref="PendingApproval"/>; an admin <see cref="Active"/>ates them onto a merchant or
/// <see cref="Rejected"/>s them; a rejected applicant may resubmit (back to <see cref="PendingApproval"/>);
/// an active account may later be <see cref="Suspended"/>. Stored as int.</summary>
public enum UserStatus
{
    PendingApproval = 1,
    Active = 2,
    Rejected = 3,
    Suspended = 4,
}
