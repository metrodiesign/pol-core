namespace Admin.Domain;

/// <summary>Lifecycle of an admin account. There is NO <c>PendingApproval</c>: admins are bootstrapped from
/// the allowlist or created by a Super, never self-approved through the producer flow (REQ-3.3).
/// <see cref="Suspended"/> revokes access (REQ-5.6).</summary>
public enum AdminStatus
{
    Active = 0,
    Suspended = 1,
}
