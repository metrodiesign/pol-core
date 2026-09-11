using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace Api.IdentityAccess;

internal sealed record BffCsrfProtected;

internal sealed class BffCsrfFilter : IEndpointFilter
{
    private static readonly HashSet<string> SafeMethods =
        new(StringComparer.OrdinalIgnoreCase) { "GET", "HEAD", "OPTIONS", "TRACE" };

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var request = context.HttpContext.Request;
        if (!SafeMethods.Contains(request.Method))
        {
            var cookie = request.Cookies[BffSessionManager.CsrfCookieName];
            var header = request.Headers[BffSessionManager.HeaderName].ToString();
            if (string.IsNullOrWhiteSpace(cookie) || string.IsNullOrWhiteSpace(header)
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header)))
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Missing or invalid CSRF token.",
                    extensions: new Dictionary<string, object?> { ["code"] = "csrf_failed" });

            var origin = request.Headers.Origin.ToString();
            if (!string.IsNullOrWhiteSpace(origin)
                && !string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Missing or invalid CSRF token.",
                    extensions: new Dictionary<string, object?> { ["code"] = "csrf_failed" });

            var session = context.HttpContext.Features.Get<BffSessionContext>();
            if (session is null || !MatchesProtectedToken(header, session.ProtectedTicket.CsrfHash))
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Missing or invalid CSRF token.",
                    extensions: new Dictionary<string, object?> { ["code"] = "csrf_failed" });
        }
        return await next(context);
    }

    private static bool MatchesProtectedToken(string token, string expectedHash)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(token)),
                Convert.FromHexString(expectedHash));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
