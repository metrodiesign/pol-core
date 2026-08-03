using System.Net.Mail;

namespace SharedKernel;

/// <summary>
/// The buyer's contact details, captured at checkout and carried onto the order (purchase-flow-completion
/// REQ-6.6/6.7/7.2). Lives here next to <see cref="Money"/> because Checkouts and Orders each own a copy of
/// these three columns and neither module may reference the other — one type here means ONE validation rule
/// instead of two that drift. Construct through <see cref="Of"/>: the constructor is private, so an
/// unvalidated contact cannot exist (the one exception is <see cref="Unspecified"/>, which stands for a row
/// that predates the columns and mirrors their DB DEFAULTs exactly).
/// </summary>
public sealed record CustomerContact
{
    /// <summary>Name written to rows that predate REQ-6.6 — the same literal as the column's DB DEFAULT.</summary>
    public const string UnknownName = "(ไม่ระบุ)";

    /// <summary>Full name. Required (REQ-6.7).</summary>
    public string Name { get; }

    /// <summary>Phone number, the primary notification channel. Required (REQ-6.7).</summary>
    public string Phone { get; }

    /// <summary>Email, optional (REQ-6.6) — the fallback notification channel.</summary>
    public string? Email { get; }

    private CustomerContact(string name, string phone, string? email)
    {
        Name = name;
        Phone = phone;
        Email = email;
    }

    /// <summary>The placeholder contact of a pre-REQ-6.6 row: name = the DB DEFAULT, no phone, no email.
    /// Deliberately NOT reachable through <see cref="Of"/> — nothing may create a blank-phone contact.</summary>
    public static readonly CustomerContact Unspecified = new(UnknownName, string.Empty, null);

    /// <summary>
    /// Validates and normalises a contact (REQ-6.7). Name and phone are required; the phone must read as a
    /// phone number (8-15 digits, optional <c>+</c>/<c>-</c>/space separators) and the email, when present,
    /// must parse as an address. Rejections are <see cref="ArgumentException"/> -> 400, and none of the
    /// messages echo the offending value.
    /// </summary>
    public static CustomerContact Of(string? name, string? phone, string? email)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Customer name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(phone))
            throw new ArgumentException("Customer phone is required.", nameof(phone));

        var trimmedName = name.Trim();
        if (trimmedName.Length > 200)
            throw new ArgumentException("Customer name is too long.", nameof(name));

        var trimmedPhone = phone.Trim();
        if (trimmedPhone.Length > 20)
            throw new ArgumentException("Customer phone is too long.", nameof(phone));
        var digits = trimmedPhone.Count(char.IsAsciiDigit);
        if (digits is < 8 or > 15 || trimmedPhone.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or '-' or ' ')))
            throw new ArgumentException("Customer phone is not a valid phone number.", nameof(phone));

        if (string.IsNullOrWhiteSpace(email))
            return new CustomerContact(trimmedName, trimmedPhone, null);

        var trimmedEmail = email.Trim();
        if (trimmedEmail.Length > 320)
            throw new ArgumentException("Customer email is too long.", nameof(email));
        // MailAddress accepts a display-name form ("Somchai <a@b>"); comparing back to the input rejects it.
        if (!MailAddress.TryCreate(trimmedEmail, out var parsed) || parsed.Address != trimmedEmail)
            throw new ArgumentException("Customer email is not a valid email address.", nameof(email));

        return new CustomerContact(trimmedName, trimmedPhone, trimmedEmail);
    }

    /// <summary>Rebuilds a contact from already-persisted columns, skipping validation — a stored row is by
    /// definition already whatever it is, and re-validating it would make a legacy row unreadable.</summary>
    public static CustomerContact FromStorage(string name, string phone, string? email) => new(name, phone, email);

    /// <summary>Where a notification for this customer goes: phone first, email second (REQ-7 / F-03).
    /// Null when the contact carries neither (a pre-REQ-6.6 row).</summary>
    public string? NotificationRecipient =>
        !string.IsNullOrWhiteSpace(Phone) ? Phone
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : null;
}
