extern alias ApiHost;
using ApiHost::Api.Admins;
using ApiHost::Api.Iam;
using ApiHost::Api.Merchants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

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
    public void Pure_admin_policy_always_selects_AdminSession()
    {
        var context = Context("admin", $"{UserSessionCookies.SessionCookieNameDevHttp}=merchant");

        Assert.Equal(SessionAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Admin, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Pure_merchant_policy_always_selects_MerchantUserSession()
    {
        var context = Context("merchant-user", $"{SessionCookies.SessionCookieNameDevHttp}=admin");

        Assert.Equal(UserSessionAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Merchant, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void Dual_console_without_admin_cookie_selects_merchant()
    {
        var context = Context("dual-console", $"{UserSessionCookies.SessionCookieNameDevHttp}=merchant");

        Assert.Equal(UserSessionAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
    }

    [Theory]
    [InlineData(SessionCookies.SessionCookieNameDevHttp)]
    [InlineData(SessionCookies.SessionCookieName)]
    public void Dual_console_admin_cookie_presence_wins_without_fallback(string cookieName)
    {
        var context = Context("dual-console",
            $"{UserSessionCookies.SessionCookieNameDevHttp}=valid-merchant; {cookieName}=invalid-admin");

        Assert.Equal(SessionAuthenticationHandler.SchemeName,
            ConsoleSessionAuthentication.SelectScheme(context));
        Assert.Equal(ConsoleAudience.Admin, context.Features.Get<SelectedConsoleAudience>()!.Value);
    }

    [Fact]
    public void OpenApi_mapping_returns_two_alternative_schemes_for_dual_console()
    {
        object[] metadata = [new AuthorizeAttribute("dual-console")];

        Assert.Equal(
            ["AdminSession", "MerchantUserSession"],
            AuthPolicyScheme.SecuritySchemeIdsFor(metadata));
    }

    [Fact]
    public void OpenApi_mapping_returns_no_scheme_for_anonymous_endpoint()
    {
        object[] metadata = [new AuthorizeAttribute("dual-console"), new AllowAnonymousAttribute()];

        Assert.Empty(AuthPolicyScheme.SecuritySchemeIdsFor(metadata));
    }
}
