namespace Merchants.Domain;

/// <summary>Lifecycle of a <see cref="MerchantUser"/>. A registrant starts
/// <see cref="PendingApproval"/>; an admin <see cref="Active"/>ates them onto a merchant or
/// <see cref="Rejected"/>s them; a rejected applicant may resubmit (back to <see cref="PendingApproval"/>);
/// an active account may later be <see cref="Suspended"/>. Stored as int.</summary>
public enum MerchantUserStatus
{
    PendingApproval = 0,
    Active = 1,
    Rejected = 2,
    Suspended = 3,
}
