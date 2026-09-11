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
            PolicyName when HasAdminCookie(context.Request.Cookies) => ConsoleAudience.Admin,
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
        return audience == ConsoleAudience.Admin
            ? SessionAuthenticationHandler.SchemeName
            : UserSessionAuthenticationHandler.SchemeName;
    }

    private static bool HasAdminCookie(IRequestCookieCollection cookies) =>
        cookies.ContainsKey(SessionCookies.SessionCookieName)
        || cookies.ContainsKey(SessionCookies.SessionCookieNameDevHttp);
}
