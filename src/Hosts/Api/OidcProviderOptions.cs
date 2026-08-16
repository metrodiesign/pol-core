using System.Security.Claims;

namespace Api;

/// <summary>
/// One confidential OIDC client registration (provider × app). Both BFF sides (AdminAuth / MerchantAuth) bind a
/// <c>Providers</c> dictionary of these; the dictionary KEY ("Google"/"Microsoft") selects the provider-specific
/// wiring in the side's Add*OidcAuthentication. <c>ClientSecret</c> is a real secret — injected via
/// <c>{Side}__Providers__{Provider}__ClientSecret</c>, never committed, never logged. A blank <c>ClientId</c>
/// disables the provider (its scheme is skipped) instead of faulting the host.
/// </summary>
internal sealed class OidcProviderOptions
{
    public string Authority { get; init; } = "";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string CallbackPath { get; init; } = "";
    /// <summary>Google-only: hosted-domain (<c>hd</c>) guard; blank = any verified Google account.</summary>
    public string HostedDomain { get; init; } = "";
    /// <summary>Microsoft-only: OPTIONAL extra <c>tid</c> allowlist layered on top of the tenant-pinned Authority;
    /// empty = no tid gate (tenant isolation already comes from issuer == discovery metadata issuer).</summary>
    public string[] AllowedTenants { get; init; } = [];
}

/// <summary>
/// The Microsoft Entra ID (v2.0) deltas from the Google wiring, shared by both BFF sides:
/// <list type="bullet">
/// <item><b>Issuer</b>: framework default — the handler compares the token's <c>iss</c> against the issuer in the
/// Authority's discovery metadata. The Authority MUST be tenant-pinned (workforce
/// <c>login.microsoftonline.com/{tenantId}/v2.0</c> or CIAM <c>{name}.ciamlogin.com/{tenantId}/v2.0</c>) so that
/// metadata issuer is a literal, giving tenant isolation with NO custom validator. Do not reintroduce an
/// <c>IssuerValidator</c>: multi-tenant Authorities are rejected at boot (RequireOidcProviders), so the template
/// -issuer problem the old custom validator solved no longer exists.</item>
/// <item><b>Subject</b>: use <c>oid</c> (stable per user) — Entra's <c>sub</c> is pairwise PER APP REGISTRATION, so
/// recreating the app registration would orphan every account keyed on it.</item>
/// <item><b>Email</b>: Entra emits no <c>email_verified</c>, and <c>email</c> only when the optional claim is
/// configured on the app registration; fall back to an @-shaped <c>preferred_username</c>.</item>
/// </list>
/// </summary>
internal static class MicrosoftOidc
{
    /// <summary>The Providers dictionary key that selects this wiring.</summary>
    public const string ProviderName = "Microsoft";

    public static bool Is(string providerName) =>
        string.Equals(providerName, ProviderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The OPTIONAL AllowedTenants gate, run in <c>OnTokenValidated</c> (both planes). Active only when
    /// the allowlist is non-empty — a tenant-pinned Authority with an empty allowlist must admit its tenant's
    /// logins (tenant isolation already comes from issuer validation). Returns the failure reason, or null to
    /// admit.</summary>
    public static string? TenantGate(ClaimsPrincipal? principal, string[] allowedTenants)
    {
        if (allowedTenants.Length == 0)
            return null;
        var tid = principal?.FindFirst("tid")?.Value;
        if (string.IsNullOrEmpty(tid))
            return "tid-required";
        return allowedTenants.Contains(tid, StringComparer.OrdinalIgnoreCase) ? null : "tenant-not-allowed";
    }

    /// <summary>Entra subject = <c>oid</c> (never <c>sub</c> — pairwise per app registration).</summary>
    public static string? Subject(ClaimsPrincipal? principal) => principal?.FindFirst("oid")?.Value;

    public static string? Email(ClaimsPrincipal? principal)
    {
        var email = principal?.FindFirst("email")?.Value;
        if (!string.IsNullOrEmpty(email))
            return email;
        // ponytail: preferred_username can be a UPN that is not a mailbox; accept it only when @-shaped.
        // Upgrade path: a Graph lookup at callback if UPN-only tenants show up.
        var upn = principal?.FindFirst("preferred_username")?.Value;
        return upn is not null && upn.Contains('@') ? upn : null;
    }
}
