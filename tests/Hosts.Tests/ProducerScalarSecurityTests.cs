extern alias ApiHost;
using System.Text.Json;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// The Scalar/OpenAPI document transformer derives each operation's security from its authorization policy
// (Program.cs SecuritySchemeForEndpoint). This boots the real document (Development, where MapOpenApi serves
// /openapi/v1.json) and asserts the producer BFF surface is present with the ProducerSession scheme — guarding the
// producer wiring the way the rest of /scalar relies on.

file sealed class OpenApiFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development); // MapOpenApi + MapScalarApiReference are Dev-only
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Producer"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
        builder.ConfigureServices(services =>
        {
            var dispatcher = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxDispatcher));
            if (dispatcher is not null)
                services.Remove(dispatcher);
        });
    }
}

public sealed class ProducerScalarSecurityTests
{
    private static async Task<JsonElement> Document()
    {
        using var factory = new OpenApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static bool OperationRequires(JsonElement root, string path, string method, string scheme)
    {
        var op = root.GetProperty("paths").GetProperty(path).GetProperty(method);
        if (!op.TryGetProperty("security", out var security))
            return false;
        return security.EnumerateArray()
            .Any(requirement => requirement.EnumerateObject().Any(p => p.Name == scheme));
    }

    [Fact]
    public async Task The_document_advertises_the_ProducerSession_cookie_scheme()
    {
        var schemes = (await Document()).GetProperty("components").GetProperty("securitySchemes");

        Assert.True(schemes.TryGetProperty("ProducerSession", out var scheme));
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("cookie", scheme.GetProperty("in").GetString());
    }

    [Theory]
    [InlineData("/producer/me", "get")]
    [InlineData("/producer/roles", "get")]
    [InlineData("/producer/permissions", "get")]
    public async Task Producer_surface_operations_require_the_ProducerSession_scheme(string path, string method) =>
        Assert.True(OperationRequires(await Document(), path, method, "ProducerSession"));

    [Fact]
    public async Task Anonymous_producer_login_carries_no_security_requirement()
    {
        var op = (await Document()).GetProperty("paths").GetProperty("/producer/auth/login").GetProperty("get");
        Assert.False(op.TryGetProperty("security", out _)); // AllowAnonymous -> no requirement
    }

    [Fact]
    public async Task The_document_description_mentions_the_producer_surface() =>
        Assert.Contains("Producer BFF",
            (await Document()).GetProperty("info").GetProperty("description").GetString());
}
