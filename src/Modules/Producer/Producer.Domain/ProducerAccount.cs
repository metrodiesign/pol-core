using SharedKernel;

namespace Producer.Domain;

/// <summary>
/// A person who can act for a tenant (the producer-side actor — schema <c>producer</c>). Control-plane — NOT
/// under the tenant RLS predicate (mirrors <see cref="Admin.Domain.AdminAccount"/>); a producer account is its
/// own identity, and the tenant it acts for is an EXTERNAL edge (<see cref="ProducerTenantAssignment"/>), never a
/// column here. Role and tenant are decided server-side at approval, NEVER by the token (product canon 2.5).
/// <see cref="Subject"/> (Google <c>sub</c>) is the stable identity, unique across users (REQ-1.4). The user's
/// role(s) are NOT a column here — they live in <c>ProducerRoleAssignments</c> (F1). The transition guard exposes
/// ONLY the lifecycle of <see cref="ProducerAccountStatus"/> (REQ-1.5/1.6). The registrant's own person details
/// (name, id, license, phone, photo — REQ-7.1) live directly on this record: a "tenant" is the company/app, not
/// the person, so person data belongs to the person's account, never a tenant-scoped profile.
/// </summary>
public sealed class ProducerAccount : AggregateRoot<Guid>
{
    /// <summary>Stable Google subject; unique across all accounts (REQ-1.4).</summary>
    public string Subject { get; private set; } = default!;

    /// <summary>Verified email captured from the id_token. Informational — <see cref="Subject"/> is the key.</summary>
    public string Email { get; private set; } = default!;

    public ProducerAccountStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Server-computed as <c>"{FirstName} {LastName}"</c> — never supplied by the form (REQ-7.1).</summary>
    public string DisplayName { get; private set; } = default!;

    // Producer detail fields (REQ-7.1). FirstName/LastName are required (they compose DisplayName); the rest optional.
    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;
    public PersonType? PersonType { get; private set; }
    public string? IdNumber { get; private set; }
    public string? ProducerCode { get; private set; }
    public string? LicenseNumber { get; private set; }
    public string? Phone { get; private set; }

    /// <summary>Opaque server-generated key into the <c>IPhotoStore</c>; never the client filename (REQ-7.2/7.5).</summary>
    public string? PhotoObjectKey { get; private set; }

    /// <summary>The validated, stored content-type served back with <c>nosniff</c> (REQ-7.5).</summary>
    public string? PhotoContentType { get; private set; }

    /// <summary>The persisted <c>DisplayName</c> column is 200 chars (see EF config); two 200-char names concatenated
    /// would overflow it, so the computed value is clamped defensively.</summary>
    private const int DisplayNameMaxLength = 200;

    private ProducerAccount() { }

    private ProducerAccount(Guid id, string subject, string email, DateTime createdAt) : base(id)
    {
        Subject = subject;
        Email = email;
        Status = ProducerAccountStatus.PendingApproval;
        CreatedAt = createdAt;
        // SetDetails runs immediately after Register in the handler and fills these; the blank-name guard there
        // throws before any blank ever persists. "" keeps the NOT NULL columns valid in the transient window.
        FirstName = string.Empty;
        LastName = string.Empty;
        DisplayName = string.Empty;
    }

    /// <summary>A new applicant registering after Google sign-in (REQ-4.1). Starts <see cref="ProducerAccountStatus.PendingApproval"/>;
    /// an admin binds the tenant (a <see cref="ProducerTenantAssignment"/>) at approval (REQ-6). The person details are
    /// applied next via <see cref="SetDetails"/> / <see cref="SetPhoto"/> so the registration handler controls them.</summary>
    public static ProducerAccount Register(string subject, string email, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new ProducerAccount(Guid.NewGuid(), subject.Trim(), email.Trim(), now);
    }

    /// <summary>Sets/overwrites the producer detail fields from the (corrected) registration form (REQ-5.3/7.1).
    /// DisplayName is recomputed from the required first/last name.</summary>
    public void SetDetails(string firstName, string lastName, PersonType? personType,
        string? idNumber, string? producerCode, string? licenseNumber, string? phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        DisplayName = ComposeDisplayName(FirstName, LastName);
        PersonType = personType;
        IdNumber = Trim(idNumber);
        ProducerCode = Trim(producerCode);
        LicenseNumber = Trim(licenseNumber);
        Phone = Trim(phone);
    }

    private static string ComposeDisplayName(string firstName, string lastName)
    {
        var composed = $"{firstName.Trim()} {lastName.Trim()}";
        return composed.Length <= DisplayNameMaxLength ? composed : composed[..DisplayNameMaxLength];
    }

    /// <summary>Records the opaque object key and stored content-type for an uploaded photo (REQ-7.2).</summary>
    public void SetPhoto(string? objectKey, string? contentType)
    {
        PhotoObjectKey = Trim(objectKey);
        PhotoContentType = Trim(contentType);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
