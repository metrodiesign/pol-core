extern alias ApiHost;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

public sealed class AdminTask3ContractTests
{
    [Fact]
    public void Http_json_serializes_sql_datetime2_values_as_explicit_utc_instants()
    {
        using var factory = new AdminTask3Factory();
        var json = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        var unspecified = new DateTime(2026, 8, 10, 8, 22, 49, DateTimeKind.Unspecified);

        Assert.Equal("\"2026-08-10T08:22:49Z\"", JsonSerializer.Serialize(unspecified, json));
    }

    [Fact]
    public async Task OpenApi_pins_admin_role_master_concurrency_and_session_idempotency_contracts()
    {
        using var factory = new AdminTask3Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");

        AssertEtag(AssertOperation(paths, "/api/v1/admins/{id}", "get", "GetAdmin"), "200");
        AssertIfMatch(AssertOperation(paths, "/api/v1/admins/{id}/tier", "post", "ChangeAdminTier"));
        AssertIfMatch(AssertOperation(paths, "/api/v1/admins/{id}/suspend", "post", "SuspendAdmin"));
        AssertIfMatch(AssertOperation(paths, "/api/v1/admins/{id}/reactivate", "post", "ReactivateAdmin"));
        AssertIfMatch(AssertOperation(paths, "/api/v1/admins/{id}/roles", "put", "SetAdminRoles"));
        AssertIfMatch(AssertOperation(paths, "/api/v1/admins/{id}/merchants", "post", "AssignMerchantToAdmin"));
        AssertIfMatch(AssertOperation(
            paths, "/api/v1/admins/{id}/merchants/{merchantId}", "delete", "UnassignMerchantFromAdmin"));

        AssertEtag(AssertOperation(paths, "/api/v1/admins/roles/{code}", "get", "GetRole"), "200");
        AssertIfMatch(AssertOperation(paths, "/api/v1/admins/roles/{code}", "put", "UpdateRole"));
        AssertIfMatch(AssertOperation(paths, "/api/v1/admins/roles/{code}", "delete", "DeleteRole"));

        var revoke = AssertOperation(
            paths, "/api/v1/admins/{id}/sessions/{sessionId}", "delete", "RevokePlatformUserSession");
        AssertRequiredHeader(revoke, "Idempotency-Key");
    }

    private static JsonElement AssertOperation(
        JsonElement paths, string path, string method, string operationId)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        return operation;
    }

    private static void AssertIfMatch(JsonElement operation) => AssertRequiredHeader(operation, "If-Match");

    private static void AssertRequiredHeader(JsonElement operation, string name)
    {
        var header = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "header"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        Assert.True(header.GetProperty("required").GetBoolean());
    }

    private static void AssertEtag(JsonElement operation, string status) =>
        Assert.True(operation.GetProperty("responses").GetProperty(status)
            .GetProperty("headers").TryGetProperty("ETag", out _));
}

file sealed class AdminTask3Factory : WebApplicationFactory<ApiHost::Program>
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
