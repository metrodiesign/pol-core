extern alias ApiHost;

using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// CORS for the separate browser SPA frontends, asserted against the single API via WebApplicationFactory.
// The one API serves BOTH SPAs (tenant + admin), so a preflight (OPTIONS) from EITHER allowlisted origin is
// echoed back; an unknown origin is not. No live database is touched — preflight short-circuits the endpoint.

file sealed class CorsFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string TenantSpaOrigin = "https://app.example.com";
    public const string AdminSpaOrigin = "https://admin.example.com";

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
                // The single API allowlists BOTH SPA origins.
                ["Cors:AllowedOrigins:0"] = TenantSpaOrigin,
                ["Cors:AllowedOrigins:1"] = AdminSpaOrigin,
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
    private static HttpRequestMessage Preflight(string origin) =>
        new(HttpMethod.Options, "/health/live")
        {
            Headers = { { "Origin", origin }, { "Access-Control-Request-Method", "POST" } },
        };

    [Theory]
    [InlineData(CorsFactory.TenantSpaOrigin)]
    [InlineData(CorsFactory.AdminSpaOrigin)]
    public async Task Preflight_from_either_allowed_spa_origin_is_echoed_back(string origin)
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(origin));

        Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Preflight_from_a_disallowed_origin_gets_no_cors_header()
    {
        using var factory = new CorsFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://evil.example.com"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
