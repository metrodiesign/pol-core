using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Producer.Application;
using Producer.Domain;

namespace Api;

/// <summary>The 201 body for a submitted registration.</summary>
public sealed record ProducerRegisterResponse(Guid TenantUserId, string Status);

/// <summary>Maps the posted multipart fields onto a <see cref="RegistrationForm"/> (REQ-7.1). Identity fields are
/// NOT read here — they come only from the verified ticket (REQ-4.2). Blank fields normalise to null; an unknown
/// personType normalises to null (no hard failure on an optional field).</summary>
internal static class ProducerRegistrationForm
{
    public static RegistrationForm From(IFormCollection form) => new(
        DisplayName: Value(form, "displayName") ?? string.Empty,
        FirstName: Value(form, "firstName"),
        LastName: Value(form, "lastName"),
        PersonType: ParsePersonType(Value(form, "personType")),
        IdNumber: Value(form, "idNumber"),
        ProducerCode: Value(form, "producerCode"),
        LicenseNumber: Value(form, "licenseNumber"),
        Phone: Value(form, "phone"));

    private static string? Value(IFormCollection form, string key)
    {
        var value = form[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static PersonType? ParsePersonType(string? value) =>
        Enum.TryParse<PersonType>(value, ignoreCase: true, out var personType) ? personType : null;
}

/// <summary>Producer registration tuning (REQ-3.2/7.4). TTL default 10 min, photo cap default 2 MB.</summary>
public sealed class ProducerRegistrationOptions
{
    public const string SectionName = "Producer:Registration";

    /// <summary>Wire-ticket lifetime; the server row's <c>ExpiresAt</c> remains the authority (REQ-3.2/3.4).</summary>
    public int TicketTtlMinutes { get; set; } = 10;

    /// <summary>Max accepted photo size in bytes (REQ-7.4).</summary>
    public long PhotoMaxBytes { get; set; } = PhotoValidation.DefaultMaxBytes;

    /// <summary>Where <see cref="LocalPhotoStore"/> writes blobs (gitignored). Relative paths resolve under the host.</summary>
    public string PhotoStoreRootPath { get; set; } = "producer-photos";
}

/// <summary>The verified identity a registration ticket carries (REQ-3.1), captured ONLY from the Google id_token at
/// the callback and returned by the client at submission. The form body can never override these.</summary>
public sealed record ProducerTicketPayload(Guid Id, string Subject, string Email, string? HostedDomain, TicketPurpose Purpose);

/// <summary>
/// Signs+encrypts the registration/correction wire ticket with ASP.NET Core Data Protection under a purpose string
/// DISTINCT from the OIDC state protector (REQ-3.1/14.4) and a built-in time limit (REQ-3.2). A tampered or expired
/// token fails to unprotect (returns false) — but the single-use authority is the server <c>RegistrationTickets</c>
/// row, consumed by a conditional UPDATE (REQ-3.4). The issuer (callback) lands in Task 5; this class is built here
/// because Task 4 consumes the ticket.
/// </summary>
internal sealed class ProducerRegistrationTickets
{
    private readonly ITimeLimitedDataProtector _protector;
    private readonly TimeSpan _ttl;

    public ProducerRegistrationTickets(IDataProtectionProvider provider, IOptions<ProducerRegistrationOptions> options)
    {
        _protector = provider.CreateProtector("Producer.RegistrationTicket.v1").ToTimeLimitedDataProtector();
        _ttl = TimeSpan.FromMinutes(options.Value.TicketTtlMinutes);
    }

    /// <summary>Issues a signed+encrypted wire ticket valid for the configured TTL (used by the callback, Task 5).</summary>
    public string Protect(ProducerTicketPayload payload) =>
        _protector.Protect(JsonSerializer.Serialize(payload), _ttl);

    /// <summary>Verifies + decodes a wire ticket. Returns false on tamper or expiry (the wire-level guard); the
    /// server row remains the single-use replay authority (REQ-3.4).</summary>
    public bool TryUnprotect(string token, out ProducerTicketPayload payload)
    {
        payload = null!;
        if (string.IsNullOrWhiteSpace(token))
            return false;
        try
        {
            var json = _protector.Unprotect(token);
            var decoded = JsonSerializer.Deserialize<ProducerTicketPayload>(json);
            if (decoded is null || decoded.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(decoded.Subject) || string.IsNullOrWhiteSpace(decoded.Email))
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
