extern alias ApiHost;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using ApiHost::Api;
using ApiHost::Api.Admins;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

// REQ-7.1-7.5: the four controls on the two endpoints that move out of /admins in task 8, written and passing
// against the PRE-MOVE code (REQ-7.5) so the move cannot silently drop one. CSRF and the Super-tier gate are
// plain endpoint filters with no queryable metadata (confirmed by enumeration — see AdminCorsGuardTests), so
// they can only be proven by driving the real pipeline. The "admin" policy's real scheme pinning is proven
// separately in AdminProvisioningAuthorizationTests; here the policy is re-pointed at a fake always-present
// test scheme so an authenticated request can get PAST that gate and reach the CSRF/tier filters without a
// live DB-backed session.

file sealed class TestAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestAdmin";
    public const string TierHeader = "X-Test-Admin-Tier";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TierHeader, out var tier) || string.IsNullOrEmpty(tier))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim("admin_tier", tier.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class ControlsFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string AdminOrigin = "https://admin.example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            ["Cors:AdminOrigins:0"] = AdminOrigin,
        }));
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>(TestAdminAuthHandler.SchemeName, _ => { });
            // Swaps only which SCHEME the "admin" policy trusts (proven for the real scheme elsewhere) so a
            // fake principal can reach the CSRF/tier filters that sit behind it.
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("admin", p => p
                .AddAuthenticationSchemes(TestAdminAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));
        });
    }
}

public sealed class AdminMerchantsEndpointControlsTests
{
    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, string? tier, string? csrf)
    {
        var request = new HttpRequestMessage(method, path);
        if (tier is not null)
            request.Headers.Add(TestAdminAuthHandler.TierHeader, tier);
        if (csrf is not null)
        {
            request.Headers.Add("Cookie", $"{SessionCookies.CsrfCookieName}={csrf}");
            request.Headers.Add(CsrfFilter.HeaderName, csrf);
        }
        if (method == HttpMethod.Post)
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return request;
    }

    [Theory]
    [InlineData("POST", "/api/v1/merchants")]
    [InlineData("GET", "/api/v1/merchants/ACME")]
    public async Task Without_a_session_the_admin_policy_rejects_the_request(string method, string path)
    {
        using var factory = new ControlsFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(BuildRequest(new HttpMethod(method), path, tier: null, csrf: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); // REQ-7.3
    }

    [Fact]
    public async Task POST_without_a_CSRF_token_is_rejected_even_with_a_Super_session()
    {
        using var factory = new ControlsFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(
            BuildRequest(HttpMethod.Post, "/api/v1/merchants", tier: "Super", csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // REQ-7.1
        Assert.Contains("CSRF", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task POST_from_a_Scoped_admin_is_rejected_even_with_a_valid_CSRF_token()
    {
        using var factory = new ControlsFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(
            BuildRequest(HttpMethod.Post, "/api/v1/merchants", tier: "Scoped", csrf: "tok-1"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // REQ-7.4: Super-only
        Assert.Contains("tier", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("POST", "/api/v1/merchants")]
    [InlineData("GET", "/api/v1/merchants/ACME")]
    public async Task Admin_origin_gets_the_credentialed_admin_CORS_policy(string method, string path)
    {
        using var factory = new ControlsFactory();
        using var client = factory.CreateClient();

        var preflight = new HttpRequestMessage(HttpMethod.Options, path)
        {
            Headers = { { "Origin", ControlsFactory.AdminOrigin }, { "Access-Control-Request-Method", method } },
        };
        var response = await client.SendAsync(preflight);

        Assert.Equal(ControlsFactory.AdminOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin"))); // REQ-7.2
    }
}
