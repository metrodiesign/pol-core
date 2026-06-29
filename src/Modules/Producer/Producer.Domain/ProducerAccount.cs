using SharedKernel;

namespace Producer.Domain;

/// <summary>
/// A person who can act for a tenant (the producer-side actor — schema <c>producer</c>). Control-plane — NOT
/// under the tenant RLS predicate (mirrors <see cref="Admin.Domain.AdminAccount"/>); a producer account is its
/// own identity, and the tenant it acts for is an EXTERNAL edge (<see cref="ProducerTenantAssignment"/>), never a
/// column here. Role and tenant are decided server-side at approval, NEVER by the token (product canon 2.5).
/// <see cref="Subject"/> (Google <c>sub</c>) is the stable identity, unique across users (REQ-1.4). The user's
/// role(s) are NOT a column here — they live in <c>ProducerRoleAssignments</c> (F1). The transition guard exposes
/// ONLY the lifecycle of <see cref="ProducerAccountStatus"/> (REQ-1.5/1.6).
/// </summary>
public sealed class ProducerAccount : AggregateRoot<Guid>
{
    /// <summary>Stable Google subject; unique across all accounts (REQ-1.4).</summary>
    public string Subject { get; private set; } = default!;

    /// <summary>Verified email captured from the id_token. Informational — <see cref="Subject"/> is the key.</summary>
    public string Email { get; private set; } = default!;

    public ProducerAccountStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private ProducerAccount() { }

    private ProducerAccount(Guid id, string subject, string email, DateTime createdAt) : base(id)
    {
        Subject = subject;
        Email = email;
        Status = ProducerAccountStatus.PendingApproval;
        CreatedAt = createdAt;
    }

    /// <summary>A new applicant registering after Google sign-in (REQ-4.1). Starts <see cref="ProducerAccountStatus.PendingApproval"/>;
    /// an admin binds the tenant (a <see cref="ProducerTenantAssignment"/>) at approval (REQ-6).</summary>
    public static ProducerAccount Register(string subject, string email, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new ProducerAccount(Guid.NewGuid(), subject.Trim(), email.Trim(), now);
    }

    /// <summary>Approves the applicant (PendingApproval→Active, REQ-1.5/6.2). The tenant edge is created separately as a
    /// <see cref="ProducerTenantAssignment"/> by the approval handler. Idempotent: re-approving an already-Active account
    /// is a no-op success (REQ-6.4). Rejecting/suspended accounts must resubmit first — any other source state throws
    /// (REQ-1.6/6.5).</summary>
    public void Approve(DateTime now)
    {
        if (Status == ProducerAccountStatus.Active)
            return; // Idempotent no-op (the tenant-match guard lives with the assignment in the handler).

        if (Status != ProducerAccountStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot approve an account in status {Status}; it must be PendingApproval.");

        Status = ProducerAccountStatus.Active;
    }

    /// <summary>Rejects a pending applicant (PendingApproval→Rejected, REQ-1.5/5.1). Any other source state throws.</summary>
    public void Reject(DateTime now)
    {
        if (Status != ProducerAccountStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot reject an account in status {Status}; it must be PendingApproval.");
        Status = ProducerAccountStatus.Rejected;
    }

    /// <summary>Re-opens a rejected applicant for review on a corrected resubmission (Rejected→PendingApproval,
    /// REQ-1.5/5.3). Any other source state throws.</summary>
    public void Resubmit(DateTime now)
    {
        if (Status != ProducerAccountStatus.Rejected)
            throw new InvalidOperationException($"Cannot resubmit an account in status {Status}; it must be Rejected.");
        Status = ProducerAccountStatus.PendingApproval;
    }

    /// <summary>Suspends an active account (Active→Suspended, REQ-1.5/12.3) — the session-killer transition. Any other
    /// source state throws.</summary>
    public void Suspend(DateTime now)
    {
        if (Status != ProducerAccountStatus.Active)
            throw new InvalidOperationException($"Cannot suspend an account in status {Status}; it must be Active.");
        Status = ProducerAccountStatus.Suspended;
    }
}
