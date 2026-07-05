extern alias ApiHost;

using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// Split CORS (REQ-10.5), asserted against the single API via WebApplicationFactory. The tenant SPA gets the
// DEFAULT policy (Bearer, no credentials) on non-admin routes; the admin SPA gets a credentialed policy bound
// ONLY to the /api/v1/admins route group. So an admin origin is echoed WITH credentials on /api/v1/admins/* but NOT on a tenant
// route, the tenant origin is echoed (no credentials) on tenant routes, and an unknown origin is never echoed.
// No live database is touched — the preflight (OPTIONS) short-circuits before auth and the endpoint.

file sealed class CorsFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string TenantSpaOrigin = "https://app.example.com";
    public const string AdminSpaOrigin = "https://admin.example.com";
    public const string ProducerSpaOrigin = "https://producer.example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Google:Audiences is read at registration (per-role policies); supply it as host config.
        builder.UseSetting("Google:Audiences:tenant", "test-client-id.apps.googleusercontent.com");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Producer"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Worker"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Cors:AllowedOrigins:0"] = TenantSpaOrigin,    // tenant (default policy, no credentials)
                ["Cors:AdminOrigins:0"] = AdminSpaOrigin,       // admin (credentialed, /api/v1/admins group only)
                ["Cors:ProducerOrigins:0"] = ProducerSpaOrigin, // producer (credentialed, /api/v1/producers group only)
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
    public async Task Tenant_origin_is_allowed_without_credentials_on_a_non_admin_route()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(CorsFactory.TenantSpaOrigin, "/health/live"));

        Assert.Equal(CorsFactory.TenantSpaOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials")); // tenant: no cookies (REQ-10.5)
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
    public async Task Producer_origin_is_allowed_with_credentials_on_a_producer_route()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        // The credentialed producer policy is bound only to /api/v1/producers by PolCorsPolicyProvider (REQ-9.1/9.3).
        var response = await client.SendAsync(Preflight(CorsFactory.ProducerSpaOrigin, "/api/v1/producers/me"));

        Assert.Equal(CorsFactory.ProducerSpaOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials"))); // cookie XHR
    }

    [Fact]
    public async Task Admin_origin_is_not_allowed_on_a_non_admin_route()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        // The credentialed admin policy is bound only to /admin — the split keeps it off the tenant surface.
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
