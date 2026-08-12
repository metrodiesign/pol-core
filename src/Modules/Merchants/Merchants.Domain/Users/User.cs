using SharedKernel;

namespace Merchants.Domain.Users;

/// <summary>
/// A person who can act for a merchant (the merchant-side actor — schema <c>merch</c>). Control-plane — NOT
/// under the merchant RLS predicate for its OWN identity row (mirrors Admins.Domain.PlatformUser);
/// <see cref="MerchantId"/> is the one merchant this account acts for, set at approval — NULL until then
/// (absorbs the former separate assignment edge, REQ-2.3). Role and merchant are decided server-side at
/// approval, NEVER by the token. <see cref="Subject"/> (Google <c>sub</c>) is the stable identity, unique
/// across users. The user's role(s) are NOT a column here — they live in <c>MerchantUserRoleAssignments</c>.
/// The registrant's own person details (name, id, license, phone, photo) live directly on this record: a
/// "merchant" is the company/app, not the person, so person data belongs to the person's account, never a
/// merchant-scoped profile.
/// </summary>
public sealed class User : AggregateRoot<Guid>
{
    /// <summary>Stable Google subject; unique across all accounts.</summary>
    public string Subject { get; private set; } = default!;

    /// <summary>Verified email captured from the id_token. Informational — <see cref="Subject"/> is the key.</summary>
    public string Email { get; private set; } = default!;

    public UserStatus Status { get; private set; }

    /// <summary>The one merchant this account acts for. NULL until admin approval sets it.</summary>
    public Guid? MerchantId { get; private set; }

    public DateTime CreatedAt { get; private set; }

    /// <summary>Optimistic resource version for Admin/Merchant profile, lifecycle, and role mutations.</summary>
    public long Version { get; private set; }

    /// <summary>Server-computed as <c>"{FirstName} {LastName}"</c> — never supplied by the form.</summary>
    public string DisplayName { get; private set; } = default!;

    // Registrant detail fields. FirstName/LastName are required (they compose DisplayName); the rest optional.
    public string FirstName { get; private set; } = default!;
    public string LastName { get; private set; } = default!;
    public IdentityType IdentityType { get; private set; }
    public string? IdentityNumber { get; private set; }

    /// <summary>The upstream sale code this account sells under — the same field the VCentralPay search
    /// contract calls <c>@SaleCode</c>. Validated to that contract's shape at <see cref="SetDetails"/>
    /// (products-external-source-of-truth REQ-4.10): 20 characters of printable ASCII, or NULL.</summary>
    public string? SaleCode { get; private set; }

    public string? LicenseNumber { get; private set; }
    public string? Phone { get; private set; }

    /// <summary>Opaque server-generated key into the <c>IPhotoStore</c>; never the client filename.</summary>
    public string? PhotoObjectKey { get; private set; }

    /// <summary>The validated, stored content-type served back with <c>nosniff</c>.</summary>
    public string? PhotoContentType { get; private set; }

    /// <summary>Opaque server-generated key for the optional KYC photo. Never exposed by a public read surface.</summary>
    public string? KycPhotoObjectKey { get; private set; }

    /// <summary>The persisted <c>DisplayName</c> column is 200 chars (see EF config); two 200-char names concatenated
    /// would overflow it, so the computed value is clamped defensively.</summary>
    private const int DisplayNameMaxLength = 200;

    private User() { }

    private User(Guid id, string subject, string email, DateTime createdAt) : base(id)
    {
        Subject = subject;
        Email = email;
        Status = UserStatus.PendingApproval;
        CreatedAt = createdAt;
        Version = 1;
        // SetDetails runs immediately after Register in the handler and fills these; the blank-name guard there
        // throws before any blank ever persists. "" keeps the NOT NULL columns valid in the transient window.
        FirstName = string.Empty;
        LastName = string.Empty;
        DisplayName = string.Empty;
    }

    /// <summary>A new applicant registering after Google sign-in. Starts <see cref="UserStatus.PendingApproval"/>
    /// with <see cref="MerchantId"/> unset; an admin sets the merchant at approval. The person details are
    /// applied next via <see cref="SetDetails"/> / <see cref="SetPhoto"/> so the registration handler controls them.</summary>
    public static User Register(string subject, string email, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return new User(Guid.NewGuid(), subject.Trim(), email.Trim(), now);
    }

    /// <summary>Registers an invited applicant already bound to the invitation's merchant.</summary>
    public static User RegisterInvited(string subject, string email, Guid merchantId, DateTime now)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
        var user = Register(subject, email, now);
        user.MerchantId = merchantId;
        return user;
    }

    /// <summary>Sets/overwrites the registrant detail fields from the (corrected) registration form.
    /// DisplayName is recomputed from the required first/last name. <paramref name="saleCode"/> is validated
    /// against the upstream contract here — this is the ONLY way a value reaches the field, and the check must
    /// live at this edge rather than at use time: <c>SqlParameter.Size = 20</c> truncates a longer value with no
    /// error, and a non-ASCII character is lost to <c>varchar</c>, so an unchecked value would silently search
    /// under somebody else's code (products-external-source-of-truth REQ-4.10, design decision #8).</summary>
    public void SetDetails(string firstName, string lastName, IdentityType personType,
        string? idNumber, string? saleCode, string? licenseNumber, string? phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        if (personType is not IdentityType.Individual and not IdentityType.Juristic)
            throw new ArgumentOutOfRangeException(nameof(personType), personType, "Unknown identity type.");
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        DisplayName = ComposeDisplayName(FirstName, LastName);
        IdentityType = personType;
        IdentityNumber = Trim(idNumber);
        SaleCode = ValidSaleCode(Trim(saleCode));
        LicenseNumber = Trim(licenseNumber);
        Phone = Trim(phone);
    }

    /// <summary>Updates only manager-editable profile fields; identity and lifecycle fields stay immutable.</summary>
    public void UpdateProfile(string firstName, string lastName, string? saleCode, string? licenseNumber, string? phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        DisplayName = ComposeDisplayName(FirstName, LastName);
        SaleCode = ValidSaleCode(Trim(saleCode));
        LicenseNumber = Trim(licenseNumber);
        Phone = Trim(phone);
        Version++;
    }

    /// <summary>The upstream contract's <c>varchar(20)</c>: at most 20 printable-ASCII characters (REQ-4.10).
    /// <see cref="ArgumentException"/> so the shared ProblemDetails handler answers 400, not 500.</summary>
    public const int SaleCodeMaxLength = 20;

    private static string? ValidSaleCode(string? saleCode)
    {
        if (saleCode is null)
            return null;
        if (saleCode.Length > SaleCodeMaxLength)
            throw new ArgumentException(
                $"SaleCode must be at most {SaleCodeMaxLength} characters.", nameof(saleCode));
        if (saleCode.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException(
                "SaleCode must contain printable ASCII characters only.", nameof(saleCode));
        return saleCode;
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

    /// <summary>Replaces the current KYC photo reference. Object lifecycle is coordinated by the outbox.</summary>
    public void SetKycPhoto(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        KycPhotoObjectKey = objectKey.Trim();
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

        if (Status == UserStatus.Active)
        {
            if (MerchantId != merchantId)
                throw new InvalidOperationException("This account is already active for a different merchant.");
            return; // Idempotent no-op.
        }

        if (Status != UserStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot approve an account in status {Status}; it must be PendingApproval.");

        Status = UserStatus.Active;
        MerchantId = merchantId;
        Version++;
    }

    /// <summary>Rejects a pending applicant (PendingApproval→Rejected). Any other source state throws.</summary>
    public void Reject(DateTime now)
    {
        if (Status != UserStatus.PendingApproval)
            throw new InvalidOperationException($"Cannot reject an account in status {Status}; it must be PendingApproval.");
        Status = UserStatus.Rejected;
        Version++;
    }

    /// <summary>Re-opens a rejected applicant for review on a corrected resubmission (Rejected→PendingApproval).
    /// Any other source state throws.</summary>
    public void Resubmit(DateTime now)
    {
        if (Status != UserStatus.Rejected)
            throw new InvalidOperationException($"Cannot resubmit an account in status {Status}; it must be Rejected.");
        Status = UserStatus.PendingApproval;
        Version++;
    }

    /// <summary>Suspends an active account (Active→Suspended) — the session-killer transition. Any other
    /// source state throws.</summary>
    public void Suspend(DateTime now)
    {
        if (Status != UserStatus.Active)
            throw new InvalidOperationException($"Cannot suspend an account in status {Status}; it must be Active.");
        Status = UserStatus.Suspended;
        Version++;
    }

    public void Reactivate(DateTime now)
    {
        if (Status != UserStatus.Suspended)
            throw new InvalidOperationException($"Cannot reactivate an account in status {Status}; it must be Suspended.");
        Status = UserStatus.Active;
        Version++;
    }

    public void EnsureVersion(long expectedVersion)
    {
        if (Version != expectedVersion)
            throw new InvalidOperationException("The merchant user changed after it was loaded.");
    }

    public void BumpVersion() => Version++;
}
