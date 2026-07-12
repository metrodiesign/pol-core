using SharedKernel;

namespace Merchants.Domain;

/// <summary>
/// A person who can act for a merchant (the merchant-side actor — schema <c>merch</c>). Control-plane — NOT
/// under the merchant RLS predicate for its OWN identity row (mirrors Admin.Domain.PlatformUser);
/// <see cref="MerchantId"/> is the one merchant this account acts for, set at approval — NULL until then
/// (absorbs the former separate assignment edge, REQ-2.3). Role and merchant are decided server-side at
/// approval, NEVER by the token. <see cref="Subject"/> (Google <c>sub</c>) is the stable identity, unique
/// across users. The user's role(s) are NOT a column here — they live in <c>MerchantUserRoleAssignments</c>.
/// The registrant's own person details (name, id, license, phone, photo) live directly on this record: a
/// "merchant" is the company/app, not the person, so person data belongs to the person's account, never a
/// merchant-scoped profile.
/// </summary>
public sealed class MerchantUser : AggregateRoot<Guid>
{
    /// <summary>Stable Google subject; unique across all accounts.</summary>
    public string Subject { get; private set; } = default!;

    /// <summary>Verified email captured from the id_token. Informational — <see cref="Subject"/> is the key.</summary>
    public string Email { get; private set; } = default!;

    public MerchantUserStatus Status { get; private set; }

    /// <summary>The one merchant this account acts for. NULL until admin approval sets it.</summary>
    public Guid? MerchantId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Server-computed as <c>"{FirstName} {LastName}"</c> — never supplied by the form.</summary>
    public string DisplayName { get; private set; } = default!;

    // Registrant detail fields. FirstName/LastName are required (they compose DisplayName); the rest optional.
    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;
    public PersonType? PersonType { get; private set; }
    public string? IdNumber { get; private set; }
    public string? ProducerCode { get; private set; }
    public string? LicenseNumber { get; private set; }
    public string? Phone { get; private set; }

    /// <summary>Opaque server-generated key into the <c>IPhotoStore</c>; never the client filename.</summary>
    public string? PhotoObjectKey { get; private set; }

    /// <summary>The validated, stored content-type served back with <c>nosniff</c>.</summary>
    public string? PhotoContentType { get; private set; }

    /// <summary>The persisted <c>DisplayName</c> column is 200 chars (see EF config); two 200-char names concatenated
    /// would overflow it, so the computed value is clamped defensively.</summary>
    private const int DisplayNameMaxLength = 200;

    private MerchantUser() { }

    private MerchantUser(Guid id, string subject, string email, DateTime createdAt) : base(id)
    {
        Subject = subject;
        Email = email;
        Status = MerchantUserStatus.PendingApproval;
        CreatedAt = createdAt;
        // SetDetails runs immediately after Register in the handler and fills these; the blank-name guard there
        // throws before any blank ever persists. "" keeps the NOT NULL columns valid in the transient window.
        FirstName = string.Empty;
        LastName = string.Empty;
        DisplayName = string.Empty;
    }

    /// <summary>A new applicant registering after Google sign-in. Starts <see cref="MerchantUserStatus.PendingApproval"/>
    /// with <see cref="MerchantId"/> unset; an admin sets the merchant at approval. The person details are
    /// applied next via <see cref="SetDetails"/> / <see cref="SetPhoto"/> so the registration handler controls them.</summary>
    public static MerchantUser Register(string subject, string email, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new MerchantUser(Guid.NewGuid(), subject.Trim(), email.Trim(), now);
    }

    /// <summary>Sets/overwrites the registrant detail fields from the (corrected) registration form.
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

    /// <summary>Records the opaque object key and stored content-type for an uploaded photo.</summary>
    public void SetPhoto(string? objectKey, string? contentType)
    {
        PhotoObjectKey = Trim(objectKey);
        PhotoContentType = Trim(contentType);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Approves the applicant onto a merchant (PendingApproval→Active) and sets <see cref="MerchantId"/>
    /// (absorbs the former separate assignment edge, REQ-2.3/9.2). Idempotent: re-approving an already-Active
    /// account onto the SAME merchant is a no-op success; re-approving onto a DIFFERENT merchant throws (the
    /// merchant-match guard formerly lived with the assignment, now lives here). Rejecting/suspended accounts
    /// must resubmit first — any other source state throws.</summary>
    public void Approve(Guid merchantId, DateTime now)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));

        if (Status == MerchantUserStatus.Active)
        {
            if (MerchantId != merchantId)
                throw new InvalidOperationException("This account is already active for a different merchant.");
            return; // Idempotent no-op.
        }

        if (Status != MerchantUserStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot approve an account in status {Status}; it must be PendingApproval.");

        Status = MerchantUserStatus.Active;
        MerchantId = merchantId;
    }

    /// <summary>Rejects a pending applicant (PendingApproval→Rejected). Any other source state throws.</summary>
    public void Reject(DateTime now)
    {
        if (Status != MerchantUserStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot reject an account in status {Status}; it must be PendingApproval.");
        Status = MerchantUserStatus.Rejected;
    }

    /// <summary>Re-opens a rejected applicant for review on a corrected resubmission (Rejected→PendingApproval).
    /// Any other source state throws.</summary>
    public void Resubmit(DateTime now)
    {
        if (Status != MerchantUserStatus.Rejected)
            throw new InvalidOperationException($"Cannot resubmit an account in status {Status}; it must be Rejected.");
        Status = MerchantUserStatus.PendingApproval;
    }

    /// <summary>Suspends an active account (Active→Suspended) — the session-killer transition. Any other
    /// source state throws.</summary>
    public void Suspend(DateTime now)
    {
        if (Status != MerchantUserStatus.Active)
            throw new InvalidOperationException($"Cannot suspend an account in status {Status}; it must be Active.");
        Status = MerchantUserStatus.Suspended;
    }
}
