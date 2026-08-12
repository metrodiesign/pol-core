extern alias ApiHost;
using System.Net;
using System.Text.Json;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

public sealed class GovernanceEndpointTests
{
    public static TheoryData<string, string> ProtectedRoutes() => new()
    {
        { "GET", "/api/v1/approvals" },
        { "GET", "/api/v1/approvals/11111111-1111-1111-1111-111111111111" },
        { "POST", "/api/v1/approvals/11111111-1111-1111-1111-111111111111/approve" },
        { "POST", "/api/v1/approvals/11111111-1111-1111-1111-111111111111/reject" },
        { "GET", "/api/v1/audits" },
        { "GET", "/api/v1/audits/11111111-1111-1111-1111-111111111111" },
    };

    [Theory]
    [MemberData(nameof(ProtectedRoutes))]
    public async Task Governance_routes_require_admin_session(string method, string path)
    {
        using var factory = new GovernanceFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_pins_operation_ids_security_headers_and_read_only_audit_surface()
    {
        using var factory = new GovernanceFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var paths = root.GetProperty("paths");

        AssertOperation(paths, "/api/v1/approvals", "get", "ListApprovalRequests");
        var detail = AssertOperation(paths, "/api/v1/approvals/{approvalId}", "get", "GetApprovalRequest");
        Assert.True(detail.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));
        var approve = AssertOperation(paths, "/api/v1/approvals/{approvalId}/approve", "post", "ApproveRequest");
        AssertDecisionHeaders(approve);
        var reject = AssertOperation(paths, "/api/v1/approvals/{approvalId}/reject", "post", "RejectRequest");
        AssertDecisionHeaders(reject);
        AssertOperation(paths, "/api/v1/audits", "get", "ListAuditRecords");
        AssertOperation(paths, "/api/v1/audits/{auditId}", "get", "GetAuditRecord");
        Assert.False(paths.GetProperty("/api/v1/audits/{auditId}").TryGetProperty("put", out _));
        Assert.False(paths.GetProperty("/api/v1/audits/{auditId}").TryGetProperty("delete", out _));
    }

    private static JsonElement AssertOperation(JsonElement paths, string path, string method, string operationId)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        var security = operation.GetProperty("security").EnumerateArray().ToArray();
        Assert.Single(security);
        Assert.True(security[0].TryGetProperty("AdminSession", out _));
        return operation;
    }

    private static void AssertDecisionHeaders(JsonElement operation)
    {
        var headers = operation.GetProperty("parameters").EnumerateArray()
            .Where(x => x.GetProperty("in").GetString() == "header")
            .ToDictionary(x => x.GetProperty("name").GetString()!, StringComparer.OrdinalIgnoreCase);
        Assert.True(headers["If-Match"].GetProperty("required").GetBoolean());
        Assert.True(headers["Idempotency-Key"].GetProperty("required").GetBoolean());
        Assert.True(operation.GetProperty("responses").GetProperty("202")
            .GetProperty("headers").TryGetProperty("ETag", out _));
    }
}

file sealed class GovernanceFactory : WebApplicationFactory<ApiHost::Program>
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
