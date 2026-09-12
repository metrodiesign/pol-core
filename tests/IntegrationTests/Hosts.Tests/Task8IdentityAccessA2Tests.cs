extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

[Trait("Capability", "ApiOperations")]
public sealed class Task8IdentityAccessA2Tests
{
    [Fact]
    [Trait("Requirement", "REQ-2")]
    [Trait("Requirement", "REQ-3")]
    [Trait("Requirement", "REQ-10")]
    public async Task A2_OpenApi_publishes_account_access_and_iam_contracts()
    {
        using var factory = new Task8A2Factory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var paths = document.GetProperty("paths");

        foreach (var (path, method, operationId) in new[]
        {
            ("/api/v1/accounts", "get", "ListAccounts"),
            ("/api/v1/accounts/{accountId}", "get", "GetAccount"),
            ("/api/v1/accounts/{accountId}", "patch", "PatchAccount"),
            ("/api/v1/accounts/{accountId}/session-revocations", "post", "RevokeAccountSessions"),
            ("/api/v1/permissions", "get", "ListCanonicalPermissions"),
            ("/api/v1/roles", "get", "ListCanonicalRoles"),
            ("/api/v1/roles", "post", "CreateCanonicalRole"),
            ("/api/v1/roles/{roleId}", "get", "GetCanonicalRole"),
            ("/api/v1/roles/{roleId}", "put", "UpdateCanonicalRole"),
            ("/api/v1/accounts/{accountId}/merchant-access", "get", "ListMerchantAccess"),
            ("/api/v1/accounts/{accountId}/merchant-access/{merchantId}", "put", "ReplaceMerchantAccess"),
            ("/api/v1/accounts/{accountId}/merchant-access/{merchantId}", "delete", "RevokeMerchantAccess"),
            ("/api/v1/accounts/{accountId}/platform-access", "get", "GetPlatformAccess"),
            ("/api/v1/accounts/{accountId}/platform-access", "put", "ReplacePlatformAccess"),
        })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("summary").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(operation.GetProperty("description").GetString()));
        }

        var schemas = document.GetProperty("components").GetProperty("schemas");
        var accountPatch = schemas.GetProperty("AccountPatchRequest").GetProperty("properties");
        Assert.Contains("displayName", accountPatch.EnumerateObject().Select(x => x.Name));
        Assert.Contains("status", accountPatch.EnumerateObject().Select(x => x.Name));
        var access = schemas.GetProperty("MerchantAccessReplaceRequest").GetProperty("properties");
        Assert.Contains("dataScope", access.EnumerateObject().Select(x => x.Name));
        Assert.Contains("roleIds", access.EnumerateObject().Select(x => x.Name));
        Assert.Contains("branchIds", access.EnumerateObject().Select(x => x.Name));
        var platform = schemas.GetProperty("PlatformAccessReplaceRequest").GetProperty("properties");
        Assert.Contains("status", platform.EnumerateObject().Select(x => x.Name));
        Assert.Contains("roleIds", platform.EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    [Trait("Requirement", "REQ-2")]
    [Trait("Requirement", "REQ-3")]
    public async Task A2_mutation_surfaces_require_authentication()
    {
        using var factory = new Task8A2Factory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Patch, "/api/v1/accounts/00000000-0000-0000-0000-000000000001"),
            (HttpMethod.Post, "/api/v1/accounts/00000000-0000-0000-0000-000000000001/session-revocations"),
            (HttpMethod.Post, "/api/v1/roles"),
            (HttpMethod.Put, "/api/v1/accounts/00000000-0000-0000-0000-000000000001/platform-access"),
        })
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}

file sealed class Task8A2Factory : WebApplicationFactory<ApiHost::Program>
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
