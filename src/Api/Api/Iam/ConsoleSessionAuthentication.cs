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
            // Admin console: an employee platform token (Authorization: Bearer). The selector below routes the
            // Admin audience to the PlatformToken scheme, which binds IAdminScope from the account's authorization
            // snapshot; existing `.RequireAuthorization("admin")` call sites are unchanged.
            .AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(SchemeName)
                .RequireAuthenticatedUser())
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
            // Dual-console: an employee Bearer token without a merchant-user cookie is the Admin audience
            // (PlatformTokenAuthenticationHandler then binds IAdminScope); identity order routes are re-routed below.
            PolicyName when bearer && !HasMerchantCookie(context.Request.Cookies) => ConsoleAudience.Admin,
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
        // Admin console has exactly one credential: the employee platform token. The scheme itself challenges a
        // missing or invalid Bearer (401), so no cookie fallback exists here.
        return audience == ConsoleAudience.Admin
            ? PlatformTokenAuthenticationHandler.SchemeName
            : UserSessionAuthenticationHandler.SchemeName;
    }

    private static bool HasBearer(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    private static bool HasMerchantCookie(IRequestCookieCollection cookies) =>
        cookies.ContainsKey(UserSessionCookies.SessionCookieName)
        || cookies.ContainsKey(UserSessionCookies.SessionCookieNameDevHttp);
}
