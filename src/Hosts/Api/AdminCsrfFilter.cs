using System.Security.Cryptography;
using System.Text;

namespace Api;

/// <summary>
/// Double-submit CSRF guard for cookie-authenticated admin mutations (REQ-7.2). On an unsafe method
/// (POST/PUT/PATCH/DELETE) the request must carry an <c>X-CSRF-Token</c> header equal to the JS-readable
/// <c>adm_csrf</c> cookie the SPA received with its session; otherwise 403. Safe methods (GET/HEAD/OPTIONS) are
/// exempt — so the OIDC login/callback GETs pass untouched. The compare is constant-time. The session cookie is
/// also <c>SameSite=Lax</c> (REQ-7.3), so this is defense in depth, not the only barrier.
/// </summary>
internal sealed class AdminCsrfFilter : IEndpointFilter
{
    public const string HeaderName = "X-CSRF-Token";

    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!SafeMethods.Contains(http.Request.Method))
        {
            var cookie = http.Request.Cookies[PlatformUserSessionCookies.CsrfCookieName];
            var header = http.Request.Headers[HeaderName].ToString();
            if (string.IsNullOrEmpty(cookie) || string.IsNullOrEmpty(header) || !FixedTimeEquals(cookie, header))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing or invalid CSRF token.");
        }

        return await next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
