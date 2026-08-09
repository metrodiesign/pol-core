using System.Security.Cryptography;
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
        SaleCode: Value(form, "saleCode"),
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
    Guid OperationId = default);

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
