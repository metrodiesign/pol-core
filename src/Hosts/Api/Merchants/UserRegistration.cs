using System.Security.Cryptography;
using System.Net;
using System.Net.Mail;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Api.Merchants;

/// <summary>The 201 body for a submitted registration.</summary>
public sealed record UserRegisterResponse(Guid UserId, string Status);

/// <summary>OpenAPI-only multipart shape. Runtime reads the form directly to bound upload size first.</summary>
public sealed class UserRegistrationMultipartRequest
{
    [Required] public string Ticket { get; init; } = "";
    [Required] public string FirstName { get; init; } = "";
    [Required] public string LastName { get; init; } = "";
    [Required] public string PersonType { get; init; } = "";
    [Required] public string IdNumber { get; init; } = "";
    [Required, MaxLength(User.SaleCodeMaxLength)] public string ProducerCode { get; init; } = "";
    public string? LicenseNumber { get; init; }
    [Required] public string Phone { get; init; } = "";
    [Required] public IFormFile Photo { get; init; } = null!;
    public IFormFile? KycPhoto { get; init; }
}

/// <summary>Maps the posted multipart fields onto a <see cref="RegistrationForm"/> (REQ-7.1). Verified identity
/// fields (subject/email/hosted domain) come only from the ticket; personType is a required form field. Blank fields
/// normalise to null (empty string for required first/last name, caught by the host's required-field check).
/// DisplayName is not a form field — the domain computes it.</summary>
internal static class UserRegistrationForm
{
    public static RegistrationForm From(IFormCollection form) => new(
        FirstName: Value(form, "firstName") ?? string.Empty,
        LastName: Value(form, "lastName") ?? string.Empty,
        IdentityType: ParsePersonType(Value(form, "personType")),
        IdentityNumber: Value(form, "idNumber"),
        SaleCode: Value(form, "producerCode"),
        LicenseNumber: Value(form, "licenseNumber"),
        Phone: Value(form, "phone"));

    private static string? Value(IFormCollection form, string key)
    {
        var value = form[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static IdentityType ParsePersonType(string? value)
    {
        if (!Enum.TryParse<IdentityType>(value, ignoreCase: true, out var personType)
            || personType is not IdentityType.Individual and not IdentityType.Juristic)
            throw new ArgumentException("personType is required and must be Individual or Juristic.", nameof(value));
        return personType;
    }
}

/// <summary>Merchant-user registration tuning (REQ-3.2/7.4). TTL default 10 min, photo cap default 2 MB.</summary>
public sealed class UserRegistrationOptions
{
    public const string SectionName = "MerchantUser:Registration";

    /// <summary>Wire-ticket lifetime; enforced by the Data Protection time limit (the token is stateless — REQ-3.2).</summary>
    public int TicketTtlMinutes { get; set; } = 10;

    /// <summary>Max accepted photo size in bytes (REQ-7.4).</summary>
    public long PhotoMaxBytes { get; set; } = PhotoValidation.DefaultMaxBytes;

    /// <summary>Where <see cref="LocalPhotoStore"/> writes blobs (gitignored). Relative paths resolve under the host.</summary>
    public string PhotoStoreRootPath { get; set; } = "merchant-user-photos";
}

/// <summary>The verified identity a registration ticket carries (REQ-3.1), captured ONLY from the verified id_token at
/// the callback and returned by the client at submission. The form body can never override these. Stateless — the
/// signed+time-limited token is self-contained (no server-side row); replay/duplicate safety is the account's unique
/// (Subject) index at submit time. <c>Provider</c> is the issuing IdP slug ("google"/"microsoft").</summary>
public sealed record UserTicketPayload(
    string Subject,
    string Email,
    string? HostedDomain,
    TicketPurpose Purpose,
    string Provider = ExternalLogin.Google,
    Guid OperationId = default,
    Guid? InvitationId = null);

/// <summary>
/// Signs+encrypts the registration/correction wire ticket with ASP.NET Core Data Protection under a purpose string
/// DISTINCT from the OIDC state protector (REQ-3.1/14.4) and a built-in time limit (REQ-3.2). A tampered or expired
/// token fails to unprotect (returns false). The token is stateless — there is no server-side ticket row; a replayed
/// still-valid token is stopped at submit time by the account's unique (Subject) index (REQ-4.6).
/// </summary>
internal sealed class UserRegistrationTickets
{
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _ttl;

    public UserRegistrationTickets(IDataProtectionProvider provider, IOptions<UserRegistrationOptions> options)
    {
        _protector = provider.CreateProtector("MerchantUser.RegistrationTicket.v1").ToTimeLimitedDataProtector();
        _ttl = TimeSpan.FromMinutes(options.Value.TicketTtlMinutes);
    }

    /// <summary>Issues a signed+encrypted wire ticket valid for the configured TTL (used by the callback, Task 5).</summary>
    public string Protect(UserTicketPayload payload)
    {
        if (payload.OperationId == Guid.Empty)
            throw new ArgumentException("Registration operation id is required.", nameof(payload));
        return _protector.Protect(JsonSerializer.Serialize(payload), _ttl);
    }

    /// <summary>Verifies + decodes a wire ticket. Returns false on tamper or expiry (the wire-level guard); replay
    /// safety is the account's unique (Subject) index at submit time (REQ-4.6).</summary>
    public bool TryUnprotect(string token, out UserTicketPayload payload)
    {
        payload = null!;
        if (string.IsNullOrWhiteSpace(token))
            return false;
        try
        {
            var json = _protector.Unprotect(token);
            var decoded = JsonSerializer.Deserialize<UserTicketPayload>(json);
            if (decoded is null ||
                string.IsNullOrWhiteSpace(decoded.Subject) || string.IsNullOrWhiteSpace(decoded.Email) ||
                decoded.OperationId == Guid.Empty)
                return false;
            payload = decoded;
            return true;
        }
        catch (CryptographicException)
        {
            return false; // tampered, wrong key, or past its time limit
        }
        catch (JsonException)
        {
            return false; // decrypted to a non-payload shape
        }
    }
}

public sealed class UserInvitationOptions
{
    public const string SectionName = "MerchantUser:Invitation";
    public int TtlHours { get; init; } = 24;
    public SmtpOptions Smtp { get; init; } = new();

    public sealed class SmtpOptions
    {
        public string Host { get; init; } = "";
        public int Port { get; init; } = 587;
        public bool EnableSsl { get; init; } = true;
        public string FromAddress { get; init; } = "";
        public string Username { get; init; } = "";
        public string PasswordFile { get; init; } = "";
    }

    public static void RequireProduction(IConfiguration configuration)
    {
        var options = configuration.GetSection(SectionName).Get<UserInvitationOptions>() ?? new();
        if (options.TtlHours is < 1 or > 168 || string.IsNullOrWhiteSpace(options.Smtp.Host)
            || options.Smtp.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(options.Smtp.FromAddress)
            || string.IsNullOrWhiteSpace(options.Smtp.Username) || string.IsNullOrWhiteSpace(options.Smtp.PasswordFile))
            throw new InvalidOperationException("MerchantUser:Invitation SMTP configuration is required in production.");
    }
}

internal sealed class InvitationDeliveryProtector : IInvitationDeliveryProtector
{
    private readonly IDataProtector _protector;
    public InvitationDeliveryProtector(IDataProtectionProvider provider) =>
        _protector = provider.CreateProtector("MerchantUser.InvitationDelivery.v1");
    public string Protect(string rawToken) => _protector.Protect(rawToken);
    public bool TryUnprotect(string protectedToken, out string rawToken)
    {
        rawToken = "";
        try { rawToken = _protector.Unprotect(protectedToken); return !string.IsNullOrWhiteSpace(rawToken); }
        catch (CryptographicException) { return false; }
    }
}

internal sealed class CaptureInvitationEmailSender : IInvitationEmailSender
{
    public Task SendAsync(string email, string rawToken, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SmtpInvitationEmailSender : IInvitationEmailSender
{
    private readonly UserInvitationOptions _options;
    private readonly UserSessionOptions _session;

    public SmtpInvitationEmailSender(IOptions<UserInvitationOptions> options, IOptions<UserSessionOptions> session)
    {
        _options = options.Value;
        _session = session.Value;
    }

    public async Task SendAsync(string email, string rawToken, CancellationToken cancellationToken)
    {
        var link = BuildLink(_session.WebAppBaseUrl, rawToken);
        var smtp = _options.Smtp;
        var password = (await File.ReadAllTextAsync(smtp.PasswordFile, cancellationToken)).Trim();
        if (password.Length == 0)
            throw new InvalidOperationException("Invitation SMTP password file is empty.");

        using var message = new MailMessage(smtp.FromAddress, email)
        {
            Subject = "Merchant invitation",
            Body = $"Open this one-time invitation link:\n\n{link}",
            IsBodyHtml = false,
        };
        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            EnableSsl = smtp.EnableSsl,
            Credentials = new NetworkCredential(smtp.Username, password),
        };
        await client.SendMailAsync(message, cancellationToken);
    }

    internal static string BuildLink(string webAppBaseUrl, string rawToken)
    {
        if (!Uri.TryCreate(webAppBaseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("MerchantSession:WebAppBaseUrl must be an absolute HTTP(S) origin.");
        return $"{baseUri.ToString().TrimEnd('/')}/invite#token={Uri.EscapeDataString(rawToken)}";
    }
}
