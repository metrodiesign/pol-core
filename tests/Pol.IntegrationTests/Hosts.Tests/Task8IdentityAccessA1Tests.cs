extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

public sealed class Task8IdentityAccessA1Tests
{
    [Fact]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-2")]
    [Trait("Requirement", "REQ-10")]
    public async Task A1_OpenApi_publishes_identity_oauth_and_system_client_contracts()
    {
        using var factory = new Task8A1Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var paths = document.GetProperty("paths");

        foreach (var (path, method, operationId) in new[]
        {
            ("/.well-known/oauth-authorization-server", "get", "GetOAuthAuthorizationServerMetadata"),
            ("/.well-known/jwks.json", "get", "GetOAuthJsonWebKeySet"),
            ("/oauth/authorize", "get", "AuthorizeOAuthClient"),
            ("/oauth/revoke", "post", "RevokeOAuthToken"),
            ("/api/v1/auth/employees/callback", "get", "EmployeeOAuthCallback"),
            ("/api/v1/auth/agents/callback", "get", "AgentOAuthCallback"),
            ("/api/v1/me/sessions", "get", "ListMySessions"),
            ("/api/v1/me/sessions/{sessionId}", "delete", "RevokeMySession"),
            ("/api/v1/system-clients", "get", "ListSystemClients"),
            ("/api/v1/system-clients", "post", "CreateSystemClient"),
            ("/api/v1/system-clients/{clientId}", "get", "GetSystemClient"),
            ("/api/v1/system-clients/{clientId}", "patch", "UpdateSystemClient"),
            ("/api/v1/system-clients/{clientId}/access", "put", "ReplaceSystemClientAccess"),
            ("/api/v1/system-clients/{clientId}/keys", "get", "ListSystemClientKeys"),
            ("/api/v1/system-clients/{clientId}/keys", "post", "CreateSystemClientKey"),
            ("/api/v1/system-clients/{clientId}/keys/{keyId}", "delete", "RevokeSystemClientKey"),
        })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()));
        }

        var schemas = document.GetProperty("components").GetProperty("schemas");
        var create = schemas.GetProperty("SystemClientCreateRequest").GetProperty("properties");
        Assert.Contains("merchantId", create.EnumerateObject().Select(x => x.Name));
        Assert.Contains("clientId", create.EnumerateObject().Select(x => x.Name));
        Assert.Contains("environment", create.EnumerateObject().Select(x => x.Name));
        var key = schemas.GetProperty("ClientKeyCreateRequest").GetProperty("properties");
        Assert.Contains("jwk", key.EnumerateObject().Select(x => x.Name));
        Assert.Contains("kid", key.EnumerateObject().Select(x => x.Name));
        Assert.Contains("validFrom", key.EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-2")]
    [Trait("Requirement", "REQ-10")]
    public async Task A1_Discovery_and_Jwks_are_public_and_secret_free()
    {
        using var factory = new Task8A1Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
        });

        using var discovery = await client.GetAsync("/.well-known/oauth-authorization-server");
        var discoveryBody = await discovery.Content.ReadAsStringAsync();
        Assert.True(discovery.StatusCode == HttpStatusCode.OK, discoveryBody);
        var discoveryJson = JsonDocument.Parse(discoveryBody).RootElement;
        Assert.Equal("/oauth/token", new Uri(discoveryJson.GetProperty("token_endpoint").GetString()!).AbsolutePath);
        Assert.Equal("/.well-known/jwks.json", new Uri(discoveryJson.GetProperty("jwks_uri").GetString()!).AbsolutePath);

        using var jwks = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, jwks.StatusCode);
        var keys = JsonDocument.Parse(await jwks.Content.ReadAsStringAsync()).RootElement.GetProperty("keys");
        Assert.NotEmpty(keys.EnumerateArray());
        foreach (var key in keys.EnumerateArray())
        {
            Assert.False(key.TryGetProperty("d", out _));
            Assert.False(key.TryGetProperty("p", out _));
            Assert.False(key.TryGetProperty("q", out _));
        }
    }

    [Fact]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-2")]
    public async Task A1_System_client_surface_is_authentication_protected_and_callbacks_fail_closed()
    {
        using var factory = new Task8A1Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var clients = await client.GetAsync("/api/v1/system-clients");
        Assert.Equal(HttpStatusCode.Unauthorized, clients.StatusCode);

        using var callback = await client.GetAsync("/api/v1/auth/employees/callback");
        Assert.NotEqual(HttpStatusCode.OK, callback.StatusCode);
        Assert.NotEqual(HttpStatusCode.NoContent, callback.StatusCode);
    }
}

file sealed class Task8A1Factory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
    }
}
