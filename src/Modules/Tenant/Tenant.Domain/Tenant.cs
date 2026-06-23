using SharedKernel;

namespace Tenant.Domain;

/// <summary>
/// A service-using organization — the control-plane master record that every tenant-scoped row
/// references via its TenantId. Provisioned by the central team per reference 2.4. The primary key
/// <see cref="Entity{TId}.Id"/> IS the tenant identity used by <c>SESSION_CONTEXT('TenantId')</c> and
/// the RLS predicate, so there is no separate owner column. Non-secret flexible config
/// (branding/routing/session/...) lives verbatim in <see cref="Metadata"/> as JSON.
/// </summary>
public sealed class Tenant : AggregateRoot<Guid>
{
    /// <summary>Captive allowlist code, normalized lowercase (unique).</summary>
    public string Code { get; private set; } = default!;

    public string DisplayName { get; private set; } = default!;

    public string LegalEntityId { get; private set; } = default!;

    public TenantStatus Status { get; private set; }

    /// <summary>ISO 3166-1 alpha-2 country code (upper).</summary>
    public string Country { get; private set; } = default!;

    /// <summary>ISO 4217 alpha-3 currency code (upper), validated against <see cref="Iso4217"/>.</summary>
    public string Currency { get; private set; } = default!;

    /// <summary>Comma-separated channel codes, stored verbatim (no semantic validation in this scope).</summary>
    public string EnabledChannels { get; private set; } = default!;

    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>Flexible non-secret config (branding/routing/session/timezone/locale/...) as JSON.</summary>
    public string Metadata { get; private set; } = default!;

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Tenant() { }

    private Tenant(Guid id, string code, string displayName, string legalEntityId, string country,
        string currency, string enabledChannels, string metadata, DateTime createdAtUtc) : base(id)
    {
        Code = code;
        DisplayName = displayName;
        LegalEntityId = legalEntityId;
        Country = country;
        Currency = currency;
        EnabledChannels = enabledChannels;
        Metadata = metadata;
        Status = TenantStatus.Active;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Creates a new active tenant. Validates the allowlist, required fields and currency. Channels
    /// and metadata are stored verbatim (no semantic validation — that is the Method router's job).
    /// </summary>
    public static Tenant Create(string code, string displayName, string legalEntityId, string country,
        string currency, IReadOnlyList<string>? enabledChannels, string? metadataJson, DateTime createdAtUtc)
    {
        var normalizedCode = TenantCode.Normalize(code);
        if (!TenantCode.IsAllowed(normalizedCode))
            throw new ArgumentException($"Tenant code '{normalizedCode}' is not in the captive allowlist.", nameof(code));

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(legalEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        var normalizedCountry = country.Trim().ToUpperInvariant();
        if (normalizedCountry.Length != 2)
            throw new ArgumentException("Country must be an ISO 3166-1 alpha-2 code.", nameof(country));

        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        if (!Iso4217.IsSupported(normalizedCurrency))
            throw new ArgumentException($"Currency '{normalizedCurrency}' is not supported.", nameof(currency));

        var channels = enabledChannels is null
            ? string.Empty
            : string.Join(',', enabledChannels.Select(c => c.Trim()).Where(c => c.Length > 0));

        return new Tenant(Guid.NewGuid(), normalizedCode, displayName.Trim(), legalEntityId.Trim(),
            normalizedCountry, normalizedCurrency, channels, metadataJson ?? "{}", createdAtUtc);
    }
}
