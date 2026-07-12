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
// /openapi/v1.json) and asserts the merchant-user BFF surface is present with the MerchantUserSession scheme —
// guarding the merchant-user wiring the way the rest of /scalar relies on.

file sealed class OpenApiFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development); // MapOpenApi + MapScalarApiReference are Dev-only
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
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

public sealed class MerchantUserScalarSecurityTests
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
    public async Task The_document_advertises_the_MerchantUserSession_cookie_scheme()
    {
        var schemes = (await Document()).GetProperty("components").GetProperty("securitySchemes");

        Assert.True(schemes.TryGetProperty("MerchantUserSession", out var scheme));
        Assert.Equal("apiKey", scheme.GetProperty("type").GetString());
        Assert.Equal("cookie", scheme.GetProperty("in").GetString());
    }

    [Theory]
    [InlineData("/api/v1/merchants/users/me", "get")]
    [InlineData("/api/v1/merchants/users/roles", "get")]
    [InlineData("/api/v1/merchants/users/permissions", "get")]
    public async Task MerchantUser_surface_operations_require_the_MerchantUserSession_scheme(string path, string method) =>
        Assert.True(OperationRequires(await Document(), path, method, "MerchantUserSession"));

    [Fact]
    public async Task Anonymous_merchant_user_login_carries_no_security_requirement()
    {
        var op = (await Document()).GetProperty("paths").GetProperty("/api/v1/merchants/users/auth/login").GetProperty("get");
        Assert.False(op.TryGetProperty("security", out _)); // AllowAnonymous -> no requirement
    }

    [Fact]
    public async Task The_document_description_mentions_the_merchant_user_surface() =>
        Assert.Contains("MerchantUser BFF",
            (await Document()).GetProperty("info").GetProperty("description").GetString());
}
