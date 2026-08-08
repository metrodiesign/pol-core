namespace Api.Admins;

/// <summary>
/// The confidential OIDC clients for the admin BFF login (REQ-1/2/8), one <see cref="OidcProviderOptions"/> per
/// provider keyed by name ("Google"/"Microsoft"). Secrets are injected via
/// <c>AdminAuth__Providers__{Provider}__ClientSecret</c> (env / user-secrets / Vault), never committed, never logged
/// (REQ-8.1/8.3). The merchant-user side runs its own separate OIDC BFF (<c>UserOidcOptions</c>) —
/// there is no shared Google id-token Bearer plumbing left (removed with T5's single-scheme session cookie).
/// </summary>
internal sealed class AdminAuthOptions
{
    public const string SectionName = "AdminAuth";

    /// <summary>SPA path the callback redirects to on a denied/failed auth (no session), with a non-sensitive reason.</summary>
    public string ErrorPath { get; init; } = "/login-error";

    public Dictionary<string, OidcProviderOptions> Providers { get; init; } = [];
}

/// <summary>
/// Server-side session lifetime + cookie posture for the admin BFF (REQ-3/5/7). Timings drive the
/// <c>SessionPolicy</c> the domain consumes; <c>SameSite</c>/allowlist are the host's cookie + open-redirect
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
    /// <summary>Absolute origin of the admin SPA the callback redirects back to (e.g. <c>http://localhost:5200</c>).
    /// The IdP callback lands on the API origin directly (provider-scoped OIDC), so a RELATIVE returnTo/ErrorPath
    /// would otherwise resolve against the API host — a JSON 404, not the SPA. Blank = keep relative (same-origin
    /// deploy behind one host). The value is operator config, never request input — returnTo itself stays an
    /// allowlisted same-origin path, so this adds no open-redirect surface.</summary>
    public string SpaBaseUrl { get; init; } = "";
    /// <summary>Absolute origin of the Development-only Scalar UI (e.g. <c>http://localhost:5100</c>).</summary>
    public string ScalarBaseUrl { get; init; } = "";
    /// <summary>Allowlisted post-login return paths (open-redirect prevention, REQ-1.3). Same-origin paths only.</summary>
    public string[] ReturnUrlAllowlist { get; init; } = [];
}
