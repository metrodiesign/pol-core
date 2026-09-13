using Api.Admins;
using Api.Merchants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation.AspNetCore;

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
        var audience = policy switch
        {
            "admin" => ConsoleAudience.Admin,
            "merchant-user" => ConsoleAudience.Merchant,
            // Dual-console: the admin cookie, or an employee BFF cookie without a merchant-user cookie, is the
            // Admin audience (the BFF scheme then binds IAdminScope); identity order routes are re-routed below.
            PolicyName when HasAdminCookie(context.Request.Cookies)
                || (HasBffCookie(context.Request.Cookies) && !HasMerchantCookie(context.Request.Cookies))
                => ConsoleAudience.Admin,
            AdminOrIdentityOrderPolicyName => ConsoleAudience.Admin,
            _ => ConsoleAudience.Merchant,
        };
        if (policy is PolicyName or AdminOrIdentityOrderPolicyName
            && IdentityPermissionAuthorization.IsIdentityOrderRoute(context)
            && IdentityPermissionAuthorization.IsIdentityRequest(context))
        {
            context.Features.Set(new SelectedConsoleAudience(ConsoleAudience.Merchant));
            return context.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme
                : Api.IdentityAccess.BffSessionAuthenticationHandler.SchemeName;
        }
        context.Features.Set(new SelectedConsoleAudience(audience));
        if (audience != ConsoleAudience.Admin)
            return UserSessionAuthenticationHandler.SchemeName;
        // Admin console: the legacy admin cookie wins; an employee BFF cookie alone authenticates through the BFF
        // scheme (which binds IAdminScope for the Admin audience). A Bearer token never reaches the console.
        return !HasAdminCookie(context.Request.Cookies) && HasBffCookie(context.Request.Cookies)
            ? Api.IdentityAccess.BffSessionAuthenticationHandler.SchemeName
            : SessionAuthenticationHandler.SchemeName;
    }

    private static bool HasAdminCookie(IRequestCookieCollection cookies) =>
        cookies.ContainsKey(SessionCookies.SessionCookieName)
        || cookies.ContainsKey(SessionCookies.SessionCookieNameDevHttp);

    private static bool HasMerchantCookie(IRequestCookieCollection cookies) =>
        cookies.ContainsKey(UserSessionCookies.SessionCookieName)
        || cookies.ContainsKey(UserSessionCookies.SessionCookieNameDevHttp);

    internal static bool HasBffCookie(IRequestCookieCollection cookies) =>
        cookies.ContainsKey(Api.IdentityAccess.BffSessionManager.SessionCookieName)
        || cookies.ContainsKey(Api.IdentityAccess.BffSessionManager.SessionCookieNameDevHttp);

    /// <summary>True when an admin-console request is authenticated by the employee BFF cookie rather than the
    /// legacy admin cookie: the CSRF double-submit then reads the BFF pair.</summary>
    internal static bool UsesBffSession(IRequestCookieCollection cookies) =>
        !HasAdminCookie(cookies) && HasBffCookie(cookies);
}
