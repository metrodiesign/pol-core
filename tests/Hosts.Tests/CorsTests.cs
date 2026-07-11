extern alias ApiHost;

using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// Split CORS (REQ-10.5), asserted against the single API via WebApplicationFactory. T5 collapsed the old
// uncredentialed Bearer "tenant" policy into the merchant-user session cookie, so the WHOLE merchant-user funnel
// (products/carts/checkouts/orders/payments/reports AND /api/v1/merchant-users/*) now shares ONE credentialed
// DEFAULT policy; the admin SPA gets its own credentialed policy bound ONLY to the /api/v1/admins route group. So
// an admin origin is echoed WITH credentials on /api/v1/admins/* but NOT on a merchant-user route, the
// merchant-user origin is echoed WITH credentials everywhere else, and an unknown origin is never echoed.
// No live database is touched — the preflight (OPTIONS) short-circuits before auth and the endpoint.

file sealed class CorsFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string MerchantUserSpaOrigin = "https://app.example.com";
    public const string AdminSpaOrigin = "https://admin.example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Cors:AllowedOrigins:0"] = MerchantUserSpaOrigin, // merchant-user (default policy, credentialed)
                ["Cors:AdminOrigins:0"] = AdminSpaOrigin,          // admin (credentialed, /api/v1/admins group only)
            });
        });
        builder.ConfigureServices(services =>
        {
            var dispatcher = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType == typeof(OutboxDispatcher));
            if (dispatcher is not null)
                services.Remove(dispatcher);
        });
    }
}

public sealed class CorsTests
{
    private static HttpRequestMessage Preflight(string origin, string path) =>
        new(HttpMethod.Options, path)
        {
            Headers = { { "Origin", origin }, { "Access-Control-Request-Method", "POST" } },
        };

    [Fact]
    public async Task MerchantUser_origin_is_allowed_with_credentials_on_a_non_admin_route()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(CorsFactory.MerchantUserSpaOrigin, "/health/live"));

        Assert.Equal(CorsFactory.MerchantUserSpaOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials"))); // T5: cookie XHR (REQ-10.5)
    }

    [Fact]
    public async Task Admin_origin_is_allowed_with_credentials_on_an_admin_route()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(CorsFactory.AdminSpaOrigin, "/api/v1/admins/me"));

        Assert.Equal(CorsFactory.AdminSpaOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials"))); // cookie XHR (REQ-4.5)
    }

    [Fact]
    public async Task MerchantUser_origin_is_allowed_with_credentials_on_the_merchant_user_bff_route()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        // /api/v1/merchant-users/* is NOT under /api/v1/admins, so PolCorsPolicyProvider routes it to the same
        // credentialed default policy as every other merchant-user-funnel route (REQ-10.5).
        var response = await client.SendAsync(Preflight(CorsFactory.MerchantUserSpaOrigin, "/api/v1/merchant-users/me"));

        Assert.Equal(CorsFactory.MerchantUserSpaOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    [Fact]
    public async Task Admin_origin_is_not_allowed_on_a_non_admin_route()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        // The credentialed admin policy is bound only to /api/v1/admins — the split keeps it off the merchant-user surface.
        var response = await client.SendAsync(Preflight(CorsFactory.AdminSpaOrigin, "/health/live"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_from_a_disallowed_origin_gets_no_cors_header()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://evil.example.com", "/api/v1/admins/me"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
