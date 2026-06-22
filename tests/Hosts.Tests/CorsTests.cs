extern alias TenantHost;
extern alias AdminHost;

using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// CORS for the separate browser SPA frontend, asserted against the real hosts via WebApplicationFactory.
// A preflight (OPTIONS) from an allowlisted origin gets Access-Control-Allow-Origin; an unknown origin does
// not (the browser then blocks it). No live database is touched — preflight short-circuits before the endpoint.

file sealed class CorsFactory<TEntry> : WebApplicationFactory<TEntry>
    where TEntry : class
{
    public const string AllowedOrigin = "https://app.example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Producer"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Worker"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Google:ClientId"] = "test-client-id.apps.googleusercontent.com",
                // Override the committed-empty origins so the policy has something to allow.
                ["Cors:AllowedOrigins:0"] = AllowedOrigin,
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

    [Fact]
    public async Task Preflight_from_an_allowed_origin_is_echoed_back()
    {
        using var factory = new CorsFactory<TenantHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(CorsFactory<TenantHost::Program>.AllowedOrigin));

        Assert.Equal(
            CorsFactory<TenantHost::Program>.AllowedOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Preflight_from_a_disallowed_origin_gets_no_cors_header()
    {
        using var factory = new CorsFactory<TenantHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("https://evil.example.com"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Admin_preflight_from_an_allowed_origin_is_echoed_back()
    {
        using var factory = new CorsFactory<AdminHost::Program>();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight(CorsFactory<AdminHost::Program>.AllowedOrigin));

        Assert.Equal(
            CorsFactory<AdminHost::Program>.AllowedOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }
}
