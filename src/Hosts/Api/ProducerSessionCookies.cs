using Microsoft.Extensions.Options;

namespace Api;

/// <summary>
/// Reads/writes the producer session + CSRF cookies (REQ-10.2/13.1). Names are distinct from Admin's (REQ-14.4):
/// <c>__Host-prd_session</c>/<c>prd_csrf</c>. The session cookie is opaque, HttpOnly and (outside dev-http) carries
/// the <c>__Host-</c> prefix; the CSRF cookie is JS-readable for the double-submit check. dev-http (Development over
/// plain http, localhost only) drops <c>Secure</c> and the <c>__Host-</c> prefix because that prefix REQUIRES
/// Secure, which a browser rejects over http (REQ-10.3).
/// </summary>
// ponytail: DUPLICATE of Api.AdminSessionCookies (adm_* -> prd_*) — deliberate debt, do not refactor into a shared base.
internal sealed class ProducerSessionCookies
{
    public const string SessionCookieName = "__Host-prd_session";
    public const string SessionCookieNameDevHttp = "prd_session";
    public const string CsrfCookieName = "prd_csrf";

    private readonly ProducerSessionOptions _options;
    private readonly IHostEnvironment _environment;

    public ProducerSessionCookies(IOptions<ProducerSessionOptions> options, IHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    private bool IsDevHttp(HttpContext http) => _environment.IsDevelopment() && !http.Request.IsHttps;

    public string SessionName(HttpContext http) => IsDevHttp(http) ? SessionCookieNameDevHttp : SessionCookieName;

    /// <summary>Writes the session + CSRF cookies (REQ-10.2/13.1).</summary>
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

        // The CSRF cookie is JS-readable (double-submit, REQ-13.1) — so NOT HttpOnly, and never the __Host- name.
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
        http.Request.Cookies.TryGetValue(SessionName(http), out var value) && !string.IsNullOrEmpty(value) ? value : null;

    public string? ReadCsrfCookie(HttpContext http) =>
        http.Request.Cookies.TryGetValue(CsrfCookieName, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    /// <summary>Clears both cookies on logout (REQ-12.1). Attributes must match the set call for the delete to bite.</summary>
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

    // SameSite=None REQUIRES Secure; over dev-http we cannot be Secure, so fall back to Lax. Cross-site (None) is
    // only meaningful on a real https deploy (REQ-13.3).
    private SameSiteMode ResolveSameSite(bool secure)
    {
        var configured = string.Equals(_options.SameSite, "None", StringComparison.OrdinalIgnoreCase)
            ? SameSiteMode.None
            : SameSiteMode.Lax;
        return configured == SameSiteMode.None && !secure ? SameSiteMode.Lax : configured;
    }
}
