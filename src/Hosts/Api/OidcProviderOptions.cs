using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Api;

/// <summary>
/// One confidential OIDC client registration (provider × app). Both BFF sides (AdminAuth / MerchantUserAuth) bind a
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
    /// <summary>Microsoft-only: allowlisted Entra tenant ids (<c>tid</c>); empty = any tenant the Authority admits.</summary>
    public string[] AllowedTenants { get; init; } = [];
}

/// <summary>
/// The Microsoft Entra ID (v2.0) deltas from the Google wiring, shared by both BFF sides:
/// <list type="bullet">
/// <item><b>Issuer</b>: the v2 issuer is per-tenant (<c>https://login.microsoftonline.com/{tid}/v2.0</c>), and for a
/// multi-tenant Authority (<c>organizations</c>/<c>common</c>) the metadata issuer is a TEMPLATE — a literal
/// ValidIssuers list can never match. Validate the token's issuer against its OWN <c>tid</c> claim instead (+ the
/// optional AllowedTenants gate); single-tenant is already enforced by the Authority itself.</item>
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

    /// <summary>Valid iff issuer == <c>https://login.microsoftonline.com/{tid}/v2.0</c> for the token's own
    /// <c>tid</c>, and tid passes <paramref name="allowedTenants"/> (empty = any).</summary>
    public static string ValidateIssuer(string issuer, SecurityToken token, string[] allowedTenants)
    {
        var tid = token switch
        {
            JsonWebToken jwt when jwt.TryGetPayloadValue<string>("tid", out var value) => value,
            JwtSecurityToken jwt => jwt.Claims.FirstOrDefault(c => c.Type == "tid")?.Value,
            _ => null,
        };
        if (string.IsNullOrEmpty(tid)
            || !string.Equals(issuer, $"https://login.microsoftonline.com/{tid}/v2.0", StringComparison.Ordinal)
            || (allowedTenants.Length > 0 && !allowedTenants.Contains(tid, StringComparer.OrdinalIgnoreCase)))
            throw new SecurityTokenInvalidIssuerException("The id_token issuer does not match its tid claim, or the tenant is not allowed.")
            { InvalidIssuer = issuer };
        return issuer;
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
