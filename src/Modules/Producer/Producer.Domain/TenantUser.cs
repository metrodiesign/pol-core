using SharedKernel;

namespace Producer.Domain;

/// <summary>
/// A person who can act for a tenant (the producer-side actor — schema <c>producer</c>, data plane). Role and
/// tenant are decided server-side at approval, NEVER by the token (product canon 2.5). <see cref="Subject"/>
/// (Google <c>sub</c>) is the stable identity, unique across users (REQ-1.4). <see cref="TenantId"/> is NULL
/// until an approval binds it (REQ-1.3); a <see cref="TenantUsers"/> row is the only RLS-keyed producer table
/// (FILTER+BLOCK on <see cref="TenantId"/>), so a pending/rejected NULL-tenant row is visible only via the
/// pol_admin bypass (REQ-19). The user's role(s) are NOT a column here — they live in
/// <c>ProducerRoleAssignments</c> (F1). The transition guard exposes ONLY the lifecycle of
/// <see cref="TenantUserStatus"/> (REQ-1.5/1.6).
/// </summary>
public sealed class TenantUser : AggregateRoot<Guid>
{
    /// <summary>Stable Google subject; unique across all users (REQ-1.4).</summary>
    public string Subject { get; private set; } = default!;

    /// <summary>Verified email captured from the id_token. Informational — <see cref="Subject"/> is the key.</summary>
    public string Email { get; private set; } = default!;

    /// <summary>The tenant this user acts for. NULL until an approval binds it (REQ-1.3).</summary>
    public Guid? TenantId { get; private set; }

    public TenantUserStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private TenantUser() { }

    private TenantUser(Guid id, string subject, string email, DateTime createdAt) : base(id)
    {
        Subject = subject;
        Email = email;
        TenantId = null;
        Status = TenantUserStatus.PendingApproval;
        CreatedAt = createdAt;
    }

    /// <summary>A new applicant registering after Google sign-in (REQ-4.1). Starts <see cref="TenantUserStatus.PendingApproval"/>
    /// with no tenant; an admin binds the tenant at approval (REQ-6).</summary>
    public static TenantUser Register(string subject, string email, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new TenantUser(Guid.NewGuid(), subject.Trim(), email.Trim(), now);
    }

    /// <summary>Approves the applicant onto <paramref name="tenantId"/> (PendingApproval→Active, REQ-1.5/6.2). Idempotent:
    /// re-approving an already-Active user onto the SAME tenant is a no-op success (REQ-6.4). Rejecting/suspended users
    /// must resubmit first — any other source state throws (REQ-1.6/6.5).</summary>
    public void Approve(Guid tenantId, DateTime now)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId is required to approve a user.", nameof(tenantId));

        if (Status == TenantUserStatus.Active)
        {
            // Idempotent no-op only when the tenant matches; re-approving onto a different tenant is rejected.
            if (TenantId == tenantId)
                return;
            throw new InvalidOperationException("An active user is already bound to a different tenant.");
        }

        if (Status != TenantUserStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot approve a user in status {Status}; it must be PendingApproval.");

        TenantId = tenantId;
        Status = TenantUserStatus.Active;
    }

    /// <summary>Rejects a pending applicant (PendingApproval→Rejected, REQ-1.5/5.1). Any other source state throws.</summary>
    public void Reject(DateTime now)
    {
        if (Status != TenantUserStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot reject a user in status {Status}; it must be PendingApproval.");
        Status = TenantUserStatus.Rejected;
    }

    /// <summary>Re-opens a rejected applicant for review on a corrected resubmission (Rejected→PendingApproval,
    /// REQ-1.5/5.3). Any other source state throws.</summary>
    public void Resubmit(DateTime now)
    {
        if (Status != TenantUserStatus.Rejected)
            throw new InvalidOperationException($"Cannot resubmit a user in status {Status}; it must be Rejected.");
        Status = TenantUserStatus.PendingApproval;
    }

    /// <summary>Suspends an active user (Active→Suspended, REQ-1.5/12.3) — the session-killer transition. Any other
    /// source state throws.</summary>
    public void Suspend(DateTime now)
    {
        if (Status != TenantUserStatus.Active)
            throw new InvalidOperationException($"Cannot suspend a user in status {Status}; it must be Active.");
        Status = TenantUserStatus.Suspended;
    }
}
