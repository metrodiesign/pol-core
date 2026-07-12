namespace Api;

/// <summary>
/// The confidential Google OIDC client for the merchant-user BFF login (REQ-8/9/14). Fully isolated from the Admin
/// <c>Google:Oidc</c> client: a distinct scheme name, callback path, and cookie names (REQ-14.4) — and, because the
/// framework derives the correlation/nonce Data Protection purposes from the scheme name, a distinct DP purpose
/// automatically (the scheme name "MerchantUserGoogle" never shares a purpose chain with Admin's "Google"). The
/// shared key ring (<see cref="AdminDataProtection"/>) is fine: isolation comes from the purpose, not a second app
/// name. <c>ClientSecret</c> is a real secret, injected via <c>MerchantUser__Oidc__ClientSecret</c>, never
/// committed/logged (REQ-14.1/14.3).
/// </summary>
// ponytail: DUPLICATE-shaped of AdminOidcOptions (distinct section + register/hd) — deliberate.
internal sealed class MerchantUserOidcOptions
{
    public const string SectionName = "MerchantUser:Oidc";

    public string Authority { get; init; } = "https://accounts.google.com";
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";
    public string CallbackPath { get; init; } = "/api/v1/merchant-users/auth/callback";
    /// <summary>SPA path the callback redirects to on a denied/failed auth (no session), with a non-sensitive reason.</summary>
    public string ErrorPath { get; init; } = "/login-error";
    /// <summary>Google hosted-domain (<c>hd</c>) guard; blank = any verified Google account (REQ-9.2).</summary>
    public string HostedDomain { get; init; } = "";
    /// <summary>Absolute URL of the merchant-user SPA registration page the callback redirects an applicant to,
    /// carrying a signed ticket (REQ-9.4). Dev default = the merchant-user SPA dev origin; prod is that SPA's origin.</summary>
    public string RegisterUrl { get; init; } = "http://localhost:5200/register";
}

/// <summary>
/// Server-side session lifetime + cookie posture for the merchant-user BFF (REQ-10/11/13). Timings drive the
/// <c>MerchantUserSessionPolicy</c> the domain consumes; <c>SameSite</c>/allowlist are the host's cookie +
/// open-redirect posture. Defaults assume a same-site merchant-user SPA + API (REQ-13.3); set <c>SameSite=None</c>
/// for a cross-site deploy.
/// </summary>
// ponytail: DUPLICATE-shaped of AdminSessionOptions (distinct section) — deliberate.
internal sealed class MerchantUserSessionOptions
{
    public const string SectionName = "MerchantUser:Session";

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
