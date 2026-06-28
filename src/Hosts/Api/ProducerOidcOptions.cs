namespace Api;

/// <summary>
/// The confidential Google OIDC client for the producer BFF login (REQ-8/9/14). Fully isolated from the Admin
/// <c>Google:Oidc</c> client: a distinct scheme name, callback path, and cookie names (REQ-14.4) — and, because the
/// framework derives the correlation/nonce Data Protection purposes from the scheme name, a distinct DP purpose
/// automatically (the scheme name "ProducerGoogle" never shares a purpose chain with Admin's "Google"). The shared
/// key ring (<see cref="AdminDataProtection"/>) is fine: isolation comes from the purpose, not a second app name.
/// <c>ClientSecret</c> is a real secret, injected via <c>Producer__Oidc__ClientSecret</c>, never committed/logged (REQ-14.1/14.3).
/// </summary>
// ponytail: DUPLICATE-shaped of AdminOidcOptions (distinct section + producer callback/register/hd) — deliberate.
internal sealed class ProducerOidcOptions
{
    public const string SectionName = "Producer:Oidc";

    public string Authority { get; init; } = "https://accounts.google.com";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string CallbackPath { get; init; } = "/producer/auth/callback";
    /// <summary>SPA path the callback redirects to on a denied/failed auth (no session), with a non-sensitive reason.</summary>
    public string ErrorPath { get; init; } = "/login-error";
    /// <summary>Google hosted-domain (<c>hd</c>) guard; blank = any verified Google account (REQ-9.2).</summary>
    public string HostedDomain { get; init; } = "";
    /// <summary>Absolute URL of the producer SPA registration page the callback redirects an applicant to, carrying a
    /// signed ticket (REQ-9.4). Dev default = the producer SPA dev origin; prod is the producer SPA origin.</summary>
    public string RegisterUrl { get; init; } = "http://localhost:5200/register";
}

/// <summary>
/// Server-side session lifetime + cookie posture for the producer BFF (REQ-10/11/13). Timings drive the
/// <c>ProducerSessionPolicy</c> the domain consumes; <c>SameSite</c>/allowlist are the host's cookie + open-redirect
/// posture. Defaults assume a same-site producer SPA + API (REQ-13.3); set <c>SameSite=None</c> for a cross-site deploy.
/// </summary>
// ponytail: DUPLICATE-shaped of AdminSessionOptions (distinct section) — deliberate.
internal sealed class ProducerSessionOptions
{
    public const string SectionName = "Producer:Session";

    public int IdleMinutes { get; init; } = 30;
    public int AbsoluteHours { get; init; } = 8;
    public int RotationMinutes { get; init; } = 15;
    public int GraceSeconds { get; init; } = 60;
    /// <summary>Cookie SameSite: <c>Lax</c> (same-site deploy) or <c>None</c> (cross-site; forces Secure + keeps CSRF).</summary>
    public string SameSite { get; init; } = "Lax";
    public string DefaultReturnPath { get; init; } = "/";
    /// <summary>Allowlisted post-login return paths (open-redirect prevention, REQ-8.3). Same-origin paths only.</summary>
    public string[] ReturnUrlAllowlist { get; init; } = [];
}
