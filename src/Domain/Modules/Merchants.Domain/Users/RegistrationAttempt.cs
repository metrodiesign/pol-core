using SharedKernel;

namespace Merchants.Domain.Users;

/// <summary>
/// Append-only snapshot of ONE registration-form submission (registration-attempt-history REQ-1): what the
/// applicant actually sent that time, frozen before the next resubmit overwrites the live <see cref="User"/>
/// row. Bound to the user via <see cref="UserId"/>; <see cref="AttemptNo"/> is 1-based and unique per
/// user (a race loses on the DB unique index → 409). <see cref="Email"/> is the verified ticket value of THAT
/// attempt (REQ-1.2/A3), not the account's current email. The photo is a reference only (REQ-1.6) — the key
/// may dangle if a later resubmit replaces the blob.
/// </summary>
public sealed class RegistrationAttempt : Entity<Guid>
{
    public Guid UserId { get; private set; }

    public int AttemptNo { get; private set; }

    public TicketPurpose Purpose { get; private set; }

    public string FirstName { get; private set; } = default!;

    public string LastName { get; private set; } = default!;

    public IdentityType IdentityType { get; private set; }

    public string? IdentityNumber { get; private set; }

    public string? SaleCode { get; private set; }

    public string? LicenseNumber { get; private set; }

    public string? Phone { get; private set; }

    public string Email { get; private set; } = default!;

    public string? PhotoObjectKey { get; private set; }

    public string? PhotoContentType { get; private set; }

    public DateTime SubmittedAt { get; private set; }

    private RegistrationAttempt() { }

    private RegistrationAttempt(Guid id, Guid merchantUserId, int attemptNo, TicketPurpose purpose,
        string firstName, string lastName, IdentityType personType, string? idNumber, string? saleCode,
        string? licenseNumber, string? phone, string email, string? photoObjectKey, string? photoContentType,
        DateTime submittedAt) : base(id)
    {
        if (personType is not IdentityType.Individual and not IdentityType.Juristic)
            throw new ArgumentOutOfRangeException(nameof(personType), personType, "Unknown identity type.");
        UserId = merchantUserId;
        AttemptNo = attemptNo;
        Purpose = purpose;
        FirstName = firstName;
        LastName = lastName;
        IdentityType = personType;
        IdentityNumber = idNumber;
        SaleCode = saleCode;
        LicenseNumber = licenseNumber;
        Phone = phone;
        Email = email;
        PhotoObjectKey = photoObjectKey;
        PhotoContentType = photoContentType;
        SubmittedAt = submittedAt;
    }

    /// <summary>Freezes the account's post-<c>SetDetails</c> state (trimmed values, current photo key) plus the
    /// ticket's email into one immutable attempt row.</summary>
    public static RegistrationAttempt Capture(
        User account, int attemptNo, TicketPurpose purpose, string ticketEmail, DateTime submittedAt)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNo, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketEmail);
        return new RegistrationAttempt(Guid.NewGuid(), account.Id, attemptNo, purpose,
            account.FirstName, account.LastName, account.IdentityType, account.IdentityNumber, account.SaleCode,
            account.LicenseNumber, account.Phone, ticketEmail, account.PhotoObjectKey, account.PhotoContentType,
            submittedAt);
    }
}
