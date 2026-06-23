namespace Api;

/// <summary>
/// The confidential OIDC client for the admin BFF login (REQ-1/2/8). <c>ClientSecret</c> is a real secret —
/// injected via <c>Google__Oidc__ClientSecret</c> (env / user-secrets / Vault), never committed, never logged
/// (REQ-8.1/8.3). Distinct from <c>Google:Audiences</c>, which stays the public id-token-bearer plumbing for
/// the tenant SPA only.
/// </summary>
internal sealed class AdminOidcOptions
{
    public const string SectionName = "Google:Oidc";

    public string Authority { get; init; } = "https://accounts.google.com";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string CallbackPath { get; init; } = "/admin/auth/callback";
    /// <summary>SPA path the callback redirects to on a denied/failed auth (no session), with a non-sensitive reason.</summary>
    public string ErrorPath { get; init; } = "/login-error";
}

/// <summary>
/// Server-side session lifetime + cookie posture for the admin BFF (REQ-3/5/7). Timings drive the
/// <c>AdminSessionPolicy</c> the domain consumes; <c>SameSite</c>/allowlist are the host's cookie + open-redirect
/// posture. Defaults assume a same-site admin SPA + API (REQ-7.3); set <c>SameSite=None</c> for a cross-site deploy.
/// </summary>
internal sealed class AdminSessionOptions
{
    public const string SectionName = "AdminSession";

    public int IdleMinutes { get; init; } = 30;
    public int AbsoluteHours { get; init; } = 8;
    public int RotationMinutes { get; init; } = 15;
    public int GraceSeconds { get; init; } = 60;
    /// <summary>Cookie SameSite: <c>Lax</c> (same-site deploy) or <c>None</c> (cross-site; forces Secure + keeps CSRF).</summary>
    public string SameSite { get; init; } = "Lax";
    public int PreAuthTtlMinutes { get; init; } = 10;
    public string DefaultReturnPath { get; init; } = "/";
    /// <summary>Allowlisted post-login return paths (open-redirect prevention, REQ-1.3). Same-origin paths only.</summary>
    public string[] ReturnUrlAllowlist { get; init; } = [];
}
