using SharedKernel;

namespace Producer.Domain;

/// <summary>
/// The producer registrant's submitted details, one-to-one with a <see cref="TenantUser"/> (REQ-7.1). Photo BYTES
/// are stored OUTSIDE the database; only the opaque server-generated <see cref="PhotoObjectKey"/> and the stored
/// <see cref="PhotoContentType"/> live here (REQ-7.2). The detail fields are nullable — the exact required/optional
/// set is enforced at the registration form/handler, not the schema. Control-plane child row (no tenant predicate).
/// </summary>
public sealed class TenantUserProfile : Entity<Guid>
{
    public Guid TenantUserId { get; private set; }

    public string DisplayName { get; private set; } = default!;

    // Producer detail fields (REQ-7.1) — all optional at the schema level.
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public PersonType? PersonType { get; private set; }
    public string? IdNumber { get; private set; }
    public string? ProducerCode { get; private set; }
    public string? LicenseNumber { get; private set; }
    public string? Phone { get; private set; }

    /// <summary>Opaque server-generated key into the <c>IPhotoStore</c>; never the client filename (REQ-7.2/7.5).</summary>
    public string? PhotoObjectKey { get; private set; }

    /// <summary>The validated, stored content-type served back with <c>nosniff</c> (REQ-7.5).</summary>
    public string? PhotoContentType { get; private set; }

    private TenantUserProfile() { }

    private TenantUserProfile(Guid id, Guid tenantUserId, string displayName) : base(id)
    {
        TenantUserId = tenantUserId;
        DisplayName = displayName;
    }

    /// <summary>Creates a profile for <paramref name="tenantUserId"/>. Detail fields and photo are set via
    /// <see cref="SetDetails"/> / <see cref="SetPhoto"/> so the registration handler controls them.</summary>
    public static TenantUserProfile Create(Guid tenantUserId, string displayName)
    {
        if (tenantUserId == Guid.Empty)
            throw new ArgumentException("TenantUserId is required.", nameof(tenantUserId));
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        return new TenantUserProfile(Guid.NewGuid(), tenantUserId, displayName.Trim());
    }

    /// <summary>Sets/overwrites the producer detail fields from the (corrected) registration form (REQ-5.3/7.1).</summary>
    public void SetDetails(string displayName, string? firstName, string? lastName, PersonType? personType,
        string? idNumber, string? producerCode, string? licenseNumber, string? phone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        DisplayName = displayName.Trim();
        FirstName = Trim(firstName);
        LastName = Trim(lastName);
        PersonType = personType;
        IdNumber = Trim(idNumber);
        ProducerCode = Trim(producerCode);
        LicenseNumber = Trim(licenseNumber);
        Phone = Trim(phone);
    }

    /// <summary>Records the opaque object key and stored content-type for an uploaded photo (REQ-7.2).</summary>
    public void SetPhoto(string? objectKey, string? contentType)
    {
        PhotoObjectKey = Trim(objectKey);
        PhotoContentType = Trim(contentType);
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
