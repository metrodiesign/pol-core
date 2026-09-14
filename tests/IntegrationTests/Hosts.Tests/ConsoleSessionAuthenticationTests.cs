extern alias ApiHost;
using ApiHost::Api.Iam;
using ApiHost::Api.Merchants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

public sealed class ConsoleSessionAuthenticationTests
{
    private static DefaultHttpContext Context(string policy, string? cookies = null)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute(policy)),
            "test"));
        if (cookies is not null)
            context.Request.Headers.Cookie = cookies;
        return context;
    }

    [Fact]
    public void Pure_admin_policy_always_selects_the_platform_token()
    {
        var context = Context("admin", $"{UserSessionCookies.SessionCookieNameDevHttp}=merchant");

        Assert.Equal(PlatformTokenAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Admin, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Pure_merchant_policy_always_selects_MerchantUserSession()
    {
        var context = Context("merchant-user");
        context.Request.Headers.Authorization = "Bearer employee-jwt";

        Assert.Equal(UserSessionAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Merchant, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Dual_console_without_a_bearer_token_selects_merchant()
    {
        var context = Context("dual-console", $"{UserSessionCookies.SessionCookieNameDevHttp}=merchant");

        Assert.Equal(UserSessionAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Merchant, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Dual_console_merchant_cookie_wins_over_a_bearer_token()
    {
        var context = Context("dual-console", $"{UserSessionCookies.SessionCookieNameDevHttp}=valid-merchant");
        context.Request.Headers.Authorization = "Bearer employee-jwt";

        Assert.Equal(UserSessionAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Merchant, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Admin_or_identity_order_policy_rejects_merchant_cookie_and_selects_admin()
    {
        var context = Context(
            ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName,
            $"{UserSessionCookies.SessionCookieNameDevHttp}=merchant");

        Assert.Equal(PlatformTokenAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Admin, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Admin_or_identity_order_policy_forwards_marked_bearer_to_identity_platform()
    {
        var context = Context(ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName);
        context.Request.Headers.Authorization = "Bearer test-token";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(
                new AuthorizeAttribute(ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName),
                new IdentityPermissionAuthorization.IdentityOrderPermissionMarker()),
            "test"));

        Assert.Equal(PlatformTokenAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Merchant, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Admin_policy_without_any_credential_still_selects_the_platform_token_so_it_challenges()
    {
        var context = Context("admin");

        Assert.Equal(PlatformTokenAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Admin, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Dual_console_bearer_token_without_merchant_cookie_is_the_admin_audience()
    {
        var context = Context("dual-console");
        context.Request.Headers.Authorization = "Bearer employee-jwt";

        Assert.Equal(PlatformTokenAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Admin, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void OpenApi_mapping_returns_two_alternative_schemes_for_dual_console()
    {
        object[] metadata = [new AuthorizeAttribute("dual-console")];

        Assert.Equal(
            [AuthPolicyScheme.PlatformTokenSchemeId, "MerchantUserSession"],
            AuthPolicyScheme.SecuritySchemeIdsFor(metadata));
    }

    [Fact]
    public void OpenApi_mapping_for_admin_or_identity_order_excludes_merchant_user()
    {
        object[] metadata =
        [new AuthorizeAttribute(ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName)];

        Assert.Equal(
            [AuthPolicyScheme.PlatformTokenSchemeId, "IdentityPlatform"],
            AuthPolicyScheme.SecuritySchemeIdsFor(metadata));
    }

    [Fact]
    public void OpenApi_mapping_returns_no_scheme_for_anonymous_endpoint()
    {
        object[] metadata = [new AuthorizeAttribute("dual-console"), new AllowAnonymousAttribute()];

        Assert.Empty(AuthPolicyScheme.SecuritySchemeIdsFor(metadata));
    }
}
