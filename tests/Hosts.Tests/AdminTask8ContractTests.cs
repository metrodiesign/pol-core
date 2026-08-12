extern alias ApiHost;

using System.Text.Json;
using Iam.Application.ApiClients;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Notifications.Application;
using Payments.Application.AdminControlPlane;

namespace Hosts.Tests;

public sealed class AdminTask8ContractTests
{
    [Fact]
    public async Task OpenApi_pins_api_client_webhook_and_notification_contracts()
    {
        using var factory = new AdminTask8Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");

        AssertAdmin(Operation(paths, "/api/v1/api-clients", "get", "ListApiClients"));
        AssertAdmin(Operation(paths, "/api/v1/api-clients/{clientId}", "get", "GetApiClient"));
        AssertMutation(paths, "/api/v1/api-clients", "post", "CreateApiClient", etagStatus: "201");
        AssertMutation(paths, "/api/v1/api-clients/{clientId}", "put", "UpdateApiClient", ifMatch: true);
        AssertMutation(paths, "/api/v1/api-clients/{clientId}/revoke", "post", "RevokeApiClient", ifMatch: true);
        AssertMutation(paths, "/api/v1/api-clients/{clientId}/secret-rotation-requests", "post",
            "RequestApiClientSecretRotation", ifMatch: true, etagStatus: "202");
        AssertMutation(paths, "/api/v1/api-clients/secrets/{ticketId}/reveal", "post", "RevealApiClientSecret");

        AssertAdmin(Operation(paths, "/api/v1/webhooks/inbound-events", "get", "ListInboundWebhookEvents"));
        AssertAdmin(Operation(paths, "/api/v1/webhooks/inbound-events/{eventId}", "get", "GetInboundWebhookEvent"));
        AssertAdmin(Operation(paths, "/api/v1/webhooks/endpoints", "get", "ListWebhookEndpoints"));
        AssertMutation(paths, "/api/v1/webhooks/endpoints", "post", "CreateWebhookEndpoint", etagStatus: "201");
        AssertMutation(paths, "/api/v1/webhooks/endpoints/{endpointId}", "put", "UpdateWebhookEndpoint", ifMatch: true);
        AssertMutation(paths, "/api/v1/webhooks/endpoints/{endpointId}", "delete", "DeleteWebhookEndpoint", ifMatch: true);
        AssertMutation(paths, "/api/v1/webhooks/deliveries/{deliveryId}/replay", "post", "ReplayWebhookDelivery");

        AssertAdmin(Operation(paths, "/api/v1/notifications/rules", "get", "ListNotificationRules"));
        AssertMutation(paths, "/api/v1/notifications/rules", "post", "CreateNotificationRule", etagStatus: "201");
        AssertMutation(paths, "/api/v1/notifications/rules/{ruleId}", "put", "UpdateNotificationRule", ifMatch: true);
        AssertMutation(paths, "/api/v1/notifications/rules/{ruleId}", "delete", "DeleteNotificationRule", ifMatch: true);
        AssertAdmin(Operation(paths, "/api/v1/notifications/deliveries", "get", "ListNotificationDeliveries"));
        AssertAdmin(Operation(paths, "/api/v1/notifications/deliveries/{deliveryId}", "get", "GetNotificationDelivery"));
    }

    [Fact]
    public void Read_contracts_expose_no_raw_credentials_or_inbound_payload()
    {
        AssertNoProperties<ApiClientView>("Secret", "SecretHash", "Ticket", "Credential");
        AssertNoProperties<WebhookEndpointView>("SigningSecret", "Secret", "ProtectedSecret");
        AssertNoProperties<InboundWebhookEventView>("RawPayload", "Payload", "Signature", "Headers");
    }

    private static void AssertNoProperties<T>(params string[] forbidden)
    {
        var names = typeof(T).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(forbidden, name => Assert.DoesNotContain(name, names));
    }

    private static void AssertMutation(JsonElement paths, string path, string method, string operationId,
        bool ifMatch = false, string? etagStatus = null)
    {
        var operation = Operation(paths, path, method, operationId);
        AssertAdmin(operation);
        AssertRequiredHeader(operation, "Idempotency-Key");
        if (ifMatch) AssertRequiredHeader(operation, "If-Match");
        if (etagStatus is not null)
            Assert.True(operation.GetProperty("responses").GetProperty(etagStatus)
                .GetProperty("headers").TryGetProperty("ETag", out _));
    }

    private static JsonElement Operation(JsonElement paths, string path, string method, string operationId)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        return operation;
    }

    private static void AssertAdmin(JsonElement operation)
    {
        var schemes = operation.GetProperty("security").EnumerateArray()
            .SelectMany(x => x.EnumerateObject().Select(property => property.Name)).ToArray();
        Assert.Equal(["AdminSession"], schemes);
    }

    private static void AssertRequiredHeader(JsonElement operation, string name)
    {
        var header = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "header"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        Assert.True(header.GetProperty("required").GetBoolean());
    }
}

file sealed class AdminTask8Factory : WebApplicationFactory<ApiHost::Program>
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
