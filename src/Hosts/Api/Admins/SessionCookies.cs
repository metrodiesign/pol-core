using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Api.Admins;

/// <summary>Opaque session-token generation + hashing for the admin BFF. The raw token is the credential and is
/// only ever held in the cookie; the SHA-256 hash is what the store persists (REQ-3.1/11.2).</summary>
internal static class SessionTokens
{
    /// <summary>A fresh opaque 256-bit token, URL-safe for a cookie value.</summary>
    public static string NewOpaqueToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>SHA-256 of the token string — the value persisted as <c>PlatformUserSessions.TokenHash</c>.</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}

/// <summary>
/// Reads/writes the admin session + CSRF cookies (REQ-3.2/3.3/7.1). The session cookie is opaque, HttpOnly and
/// (outside dev-http) carries the <c>__Host-</c> prefix; the CSRF cookie is JS-readable for the double-submit
/// check. dev-http (Development over plain http, localhost only) drops <c>Secure</c> and the <c>__Host-</c>
/// prefix because that prefix REQUIRES Secure, which a browser rejects over http (REQ-3.3).
/// </summary>
internal sealed class SessionCookies
{
    public const string SessionCookieName = "__Host-adm_session";
    public const string SessionCookieNameDevHttp = "adm_session";
    public const string CsrfCookieName = "adm_csrf";

    private readonly AdminSessionOptions _options;
    private readonly IHostEnvironment _environment;

    public SessionCookies(IOptions<AdminSessionOptions> options, IHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    private bool IsDevHttp(HttpContext http) => _environment.IsDevelopment() && !http.Request.IsHttps;

    public string SessionName(HttpContext http) => IsDevHttp(http) ? SessionCookieNameDevHttp : SessionCookieName;

    /// <summary>Writes the session + CSRF cookies (REQ-3.2/7.1).</summary>
    public void Write(HttpContext http, string sessionToken, string csrfToken)
    {
        var secure = !IsDevHttp(http);
        var sameSite = ResolveSameSite(secure);

        http.Response.Cookies.Append(SessionName(http), sessionToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            Path = "/",
            SameSite = sameSite,
            IsEssential = true, // not subject to cookie-consent suppression — it is the auth credential
        });

        // The CSRF cookie is JS-readable (double-submit, REQ-7.1) — so NOT HttpOnly, and never the __Host- name.
        http.Response.Cookies.Append(CsrfCookieName, csrfToken, new CookieOptions
        {
            HttpOnly = false,
            Secure = secure,
            Path = "/",
            SameSite = sameSite,
            IsEssential = true,
        });
    }

    public string? ReadSessionToken(HttpContext http) =>
        http.Request.Cookies.TryGetValue(SessionName(http), out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    public string? ReadCsrfCookie(HttpContext http) =>
        http.Request.Cookies.TryGetValue(CsrfCookieName, out var value) && !string.IsNullOrEmpty(value)
            ? value
            : null;

    /// <summary>Clears both cookies on logout (REQ-6.1). Attributes must match the set call for the delete to bite.</summary>
    public void Clear(HttpContext http)
    {
        var secure = !IsDevHttp(http);
        var sameSite = ResolveSameSite(secure);
        http.Response.Cookies.Delete(SessionName(http), new CookieOptions
        {
            HttpOnly = true, Secure = secure, Path = "/", SameSite = sameSite,
        });
        http.Response.Cookies.Delete(CsrfCookieName, new CookieOptions
        {
            HttpOnly = false, Secure = secure, Path = "/", SameSite = sameSite,
        });
    }

    // SameSite=None REQUIRES Secure; over dev-http we cannot be Secure, so fall back to Lax. Cross-site
    // (None) is only meaningful on a real https deploy (REQ-7.4).
    private SameSiteMode ResolveSameSite(bool secure)
    {
        var configured = string.Equals(_options.SameSite, "None", StringComparison.OrdinalIgnoreCase)
            ? SameSiteMode.None
            : SameSiteMode.Lax;
        return configured == SameSiteMode.None && !secure ? SameSiteMode.Lax : configured;
    }
}
