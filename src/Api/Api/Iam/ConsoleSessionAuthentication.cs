using Api.Admins;
using Api.Merchants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Api.IdentityAccess;

namespace Api.Iam;

internal enum ConsoleAudience { Admin, Merchant }
internal sealed record SelectedConsoleAudience(ConsoleAudience Value);

internal static class ConsoleSessionAuthentication
{
    public const string SchemeName = "ConsoleSession";
    public const string PolicyName = "dual-console";
    public const string AdminOrIdentityOrderPolicyName = "admin-or-identity-order";

    public static IServiceCollection AddConsoleSessionAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication()
            .AddPolicyScheme(SchemeName, displayName: null,
                options => options.ForwardDefaultSelector = SelectScheme);
        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy
                .AddAuthenticationSchemes(SchemeName)
                .RequireAuthenticatedUser())
            .AddPolicy(AdminOrIdentityOrderPolicyName, policy => policy
                .AddAuthenticationSchemes(SchemeName)
                .RequireAuthenticatedUser());
        return services;
    }

    internal static string SelectScheme(HttpContext context)
    {
        var policy = context.GetEndpoint()?.Metadata.OfType<IAuthorizeData>()
            .Select(a => a.Policy)
            .LastOrDefault(p => !string.IsNullOrEmpty(p));
        var bearer = HasBearer(context.Request);
        var audience = policy switch
        {
            "admin" => ConsoleAudience.Admin,
            "merchant-user" => ConsoleAudience.Merchant,
            // Dual-console: the admin cookie, or an employee Bearer token without a merchant-user cookie, is the
            // Admin audience (PlatformTokenAuthenticationHandler then binds IAdminScope); identity order routes are re-routed below.
            PolicyName when HasAdminCookie(context.Request.Cookies)
                || (bearer && !HasMerchantCookie(context.Request.Cookies))
                => ConsoleAudience.Admin,
            AdminOrIdentityOrderPolicyName => ConsoleAudience.Admin,
            _ => ConsoleAudience.Merchant,
        };
        if (policy is PolicyName or AdminOrIdentityOrderPolicyName
            && IdentityPermissionAuthorization.IsIdentityOrderRoute(context)
            && IdentityPermissionAuthorization.IsIdentityRequest(context))
        {
            context.Features.Set(new SelectedConsoleAudience(ConsoleAudience.Merchant));
            return PlatformTokenAuthenticationHandler.SchemeName;
        }
        context.Features.Set(new SelectedConsoleAudience(audience));
        if (audience != ConsoleAudience.Admin)
            return UserSessionAuthenticationHandler.SchemeName;
        // Admin console: the legacy admin cookie wins; an employee Bearer token alone authenticates through the
        // platform token scheme (which binds IAdminScope for the Admin audience).
        return !HasAdminCookie(context.Request.Cookies) && bearer
            ? PlatformTokenAuthenticationHandler.SchemeName
            : SessionAuthenticationHandler.SchemeName;
    }

    private static bool HasBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    private static bool HasAdminCookie(IRequestCookieCollection cookies) =>
        cookies.ContainsKey(SessionCookies.SessionCookieName)
        || cookies.ContainsKey(SessionCookies.SessionCookieNameDevHttp);

    private static bool HasMerchantCookie(IRequestCookieCollection cookies) =>
        cookies.ContainsKey(UserSessionCookies.SessionCookieName)
        || cookies.ContainsKey(UserSessionCookies.SessionCookieNameDevHttp);
}
