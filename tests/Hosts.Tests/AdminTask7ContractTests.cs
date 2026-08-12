extern alias ApiHost;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Reporting.Application;

namespace Hosts.Tests;

public sealed class AdminTask7ContractTests
{
    [Fact]
    public void Transaction_projection_uses_payment_state_and_flags_order_disagreement()
    {
        Assert.Equal(("pending", null), TransactionProjectionRules.Normalize("Created", "Pending"));
        Assert.Equal(("pending", null), TransactionProjectionRules.Normalize("Redirected", "Pending"));
        Assert.Equal(("paid", null), TransactionProjectionRules.Normalize("Paid", "Paid"));
        Assert.Equal(("failed", null), TransactionProjectionRules.Normalize("Failed", "Failed"));
        Assert.Equal(("expired", null), TransactionProjectionRules.Normalize("Expired", "Expired"));
        Assert.Equal(("paid", "order_session_state_mismatch"),
            TransactionProjectionRules.Normalize("Paid", "Pending"));

        var capabilities = TransactionProjectionRules.Capabilities("paid");
        Assert.All(capabilities, capability => Assert.False(capability.Available));
        Assert.True(capabilities.Single(x => x.Code == "refund").RequiresApproval);
    }

    [Fact]
    public async Task OpenApi_pins_reporting_routes_security_queries_and_etag()
    {
        using var factory = new AdminTask7Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("paths");

        var dashboard = Operation(paths, "/api/v1/reports/dashboard", "get", "GetAdminDashboard");
        AssertAdmin(dashboard);
        AssertQuery(dashboard, "from", required: false);
        AssertQuery(dashboard, "to", required: false);
        AssertQuery(dashboard, "merchantId", required: false);

        var list = Operation(paths, "/api/v1/payments/transactions", "get", "ListAdminTransactions");
        AssertAdmin(list);
        AssertQuery(list, "filters", required: false);
        AssertQuery(list, "sort", required: false);
        AssertQuery(list, "search", required: false);

        var detail = Operation(paths, "/api/v1/payments/transactions/{paymentSessionId}", "get", "GetAdminTransaction");
        AssertAdmin(detail);
        Assert.True(detail.GetProperty("responses").GetProperty("200")
            .GetProperty("headers").TryGetProperty("ETag", out _));

        var export = Operation(paths, "/api/v1/payments/transactions/export", "get", "ExportAdminTransactions");
        AssertAdmin(export);
        AssertQuery(export, "from", required: true);
        AssertQuery(export, "to", required: true);

        AssertAdmin(Operation(paths, "/api/v1/reports/operations", "get", "GetOperationsReport"));
        AssertAdmin(Operation(paths, "/api/v1/reports/operations/export", "get", "ExportOperationsReport"));

        var reconciliation = Operation(paths, "/api/v1/reports/reconciliation", "get", "GetReconciliationReport");
        var schemes = Schemes(reconciliation);
        Assert.Contains("AdminSession", schemes);
        Assert.Contains("MerchantUserSession", schemes);
    }

    private static JsonElement Operation(JsonElement paths, string path, string method, string operationId)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        return operation;
    }

    private static void AssertQuery(JsonElement operation, string name, bool required)
    {
        var query = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "query"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        Assert.Equal(required, query.TryGetProperty("required", out var value) && value.GetBoolean());
    }

    private static void AssertAdmin(JsonElement operation) =>
        Assert.Equal(["AdminSession"], Schemes(operation));

    private static HashSet<string> Schemes(JsonElement operation) => operation.GetProperty("security")
        .EnumerateArray().SelectMany(x => x.EnumerateObject().Select(p => p.Name))
        .ToHashSet(StringComparer.Ordinal);
}

file sealed class AdminTask7Factory : WebApplicationFactory<ApiHost::Program>
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
