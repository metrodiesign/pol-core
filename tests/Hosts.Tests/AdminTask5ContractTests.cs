extern alias ApiHost;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

public sealed class AdminTask5ContractTests
{
    [Fact]
    public async Task OpenApi_pins_dual_console_user_reads_and_admin_merchant_identity_mutations()
    {
        using var factory = new AdminTask5Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");

        AssertDualConsole(AssertOperation(paths, "/api/v1/merchants/users", "get", "ListMerchantUsers"));
        AssertDualConsole(AssertOperation(paths, "/api/v1/merchants/users/{merchantUserId}", "get", "GetMerchantUser"));
        AssertAdminConsole(AssertOperation(paths,
            "/api/v1/merchants/{merchantId}/users/{merchantUserId}/edit", "get",
            "GetMerchantUserEditAdmin"));

        AssertHeader(AssertOperation(paths, "/api/v1/merchants/{merchantId}/user-invitations", "post",
            "InviteMerchantUserAdmin"), "Idempotency-Key");
        AssertHeader(AssertOperation(paths, "/api/v1/merchants/{merchantId}/users/{merchantUserId}", "put",
            "UpdateMerchantUserAdmin"), "If-Match");

        AssertOperation(paths, "/api/v1/merchants/{merchantId}/roles", "get", "ListAdminMerchantRoles");
        AssertOperation(paths, "/api/v1/merchants/{merchantId}/roles/{code}", "get", "GetAdminMerchantRole");
        AssertOperation(paths, "/api/v1/merchants/{merchantId}/permissions", "get", "ListAdminMerchantPermissions");
        AssertOperation(paths, "/api/v1/merchants/{merchantId}/roles", "post", "CreateAdminMerchantRole");
        AssertHeader(AssertOperation(paths, "/api/v1/merchants/{merchantId}/roles/{code}", "put",
            "UpdateAdminMerchantRole"), "If-Match");
        AssertHeader(AssertOperation(paths, "/api/v1/merchants/{merchantId}/roles/{code}", "delete",
            "DeleteAdminMerchantRole"), "If-Match");
        AssertHeader(AssertOperation(paths, "/api/v1/merchants/{merchantId}/users/{merchantUserId}/roles", "put",
            "SetAdminMerchantUserRoles"), "If-Match");

        AssertHeader(AssertOperation(paths, "/api/v1/admins/merchants/users/{merchantUserId}/approve", "post",
            "ApproveMerchantUser"), "If-Match");
        AssertHeader(AssertOperation(paths, "/api/v1/admins/merchants/users/{merchantUserId}/approve", "post",
            "ApproveMerchantUser"), "Idempotency-Key");
        AssertHeader(AssertOperation(paths, "/api/v1/admins/merchants/users/{merchantUserId}/reject", "post",
            "RejectMerchantUser"), "If-Match");
        AssertHeader(AssertOperation(paths, "/api/v1/admins/merchants/users/{merchantUserId}/reject", "post",
            "RejectMerchantUser"), "Idempotency-Key");
    }

    private static JsonElement AssertOperation(
        JsonElement paths, string path, string method, string operationId)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        return operation;
    }

    private static void AssertHeader(JsonElement operation, string name)
    {
        var header = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "header"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        Assert.True(header.GetProperty("required").GetBoolean());
    }

    private static void AssertDualConsole(JsonElement operation)
    {
        var schemes = operation.GetProperty("security").EnumerateArray()
            .SelectMany(x => x.EnumerateObject().Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("AdminSession", schemes);
        Assert.Contains("MerchantUserSession", schemes);
    }

    private static void AssertAdminConsole(JsonElement operation)
    {
        var schemes = operation.GetProperty("security").EnumerateArray()
            .SelectMany(x => x.EnumerateObject().Select(p => p.Name))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["AdminSession"], schemes);
    }
}

file sealed class AdminTask5Factory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
    }
}
