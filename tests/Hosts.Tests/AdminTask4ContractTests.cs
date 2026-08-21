extern alias ApiHost;

using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Payments.Application.AdminControlPlane;
using Persistence.MerchantRuntime.Payments;

namespace Hosts.Tests;

public sealed class AdminTask4ContractTests
{
    [Fact]
    public async Task PaymentCapabilityQueries_pin_five_admin_queries_and_policy_mutations()
    {
        using var factory = new AdminTask4Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");

        AssertOperation(paths, "/api/v1/merchants/users", "get", "ListMerchantUsers");
        AssertOperation(paths, "/api/v1/payments/merchants/{merchantId}/methods", "get",
            "ListMerchantPaymentMethods");
        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/merchants/{merchantId}/users/{userId}/methods", "get",
            "ListMerchantUserPaymentMethods"), "200");
        AssertOperation(paths,
            "/api/v1/payments/merchants/{merchantId}/users/{userId}/methods/{method}/resolution", "get",
            "ResolveMerchantUserPaymentMethod");
        var options = AssertOperation(paths,
            "/api/v1/payments/merchants/{merchantId}/users/{userId}/methods/{method}/options", "get",
            "ResolveMerchantUserPaymentOptions");
        Assert.Contains(options.GetProperty("parameters").EnumerateArray(), x =>
            x.GetProperty("name").GetString() == "provider" && x.GetProperty("required").GetBoolean());

        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/merchants/{merchantId}/methods/{method}", "get",
            "GetMerchantPaymentMethodPolicy"), "200");
        AssertMutation(paths, "/api/v1/payments/merchants/{merchantId}/methods/{method}", "put",
            "SetMerchantPaymentMethodPolicy", etag: true, idempotency: true);
        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/merchants/{merchantId}/users/{userId}/methods/{method}", "get",
            "GetMerchantUserPaymentMethodPolicy"), "200");
        AssertMutation(paths,
            "/api/v1/payments/merchants/{merchantId}/users/{userId}/methods/{method}", "put",
            "SetMerchantUserPaymentMethodPolicy", etag: true, idempotency: true);
    }

    [Fact]
    public async Task PaymentProviderCapability_routes_pin_paired_get_put_etag_and_idempotency()
    {
        using var factory = new AdminTask4Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");

        AssertResponseEtag(AssertOperation(paths, "/api/v1/payments/methods/{method}", "get",
            "GetPaymentMethodCapability"), "200");
        AssertMutation(paths, "/api/v1/payments/methods/{method}", "put",
            "SetPaymentMethodCapability", etag: true, idempotency: true);
        AssertResponseEtag(AssertOperation(paths, "/api/v1/payments/providers/{providerCode}", "get",
            "GetPaymentProviderCapability"), "200");
        AssertMutation(paths, "/api/v1/payments/providers/{providerCode}", "put",
            "SetPaymentProviderCapability", etag: true, idempotency: true);
        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/providers/{providerCode}/methods/{method}", "get",
            "GetPaymentProviderMethodCapability"), "200");
        AssertMutation(paths, "/api/v1/payments/providers/{providerCode}/methods/{method}", "put",
            "SetPaymentProviderMethodCapability", etag: true, idempotency: true);
        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}", "get",
            "GetPaymentProviderMethodOptionCapability"), "200");
        AssertMutation(paths,
            "/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}", "put",
            "SetPaymentProviderMethodOptionCapability", etag: true, idempotency: true);
    }

    [Fact]
    public async Task PaymentAccountCapability_routes_pin_scoped_get_put_without_credentials()
    {
        using var factory = new AdminTask4Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");

        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/psp-connections/{connectionId}/methods/{method}", "get",
            "GetPaymentAccountMethodCapability"), "200");
        AssertMutation(paths, "/api/v1/payments/psp-connections/{connectionId}/methods/{method}", "put",
            "SetPaymentAccountMethodCapability", etag: true, idempotency: true);
        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/psp-connections/{connectionId}/methods/{method}/options/{option}", "get",
            "GetPaymentAccountMethodOptionCapability"), "200");
        AssertMutation(paths,
            "/api/v1/payments/psp-connections/{connectionId}/methods/{method}/options/{option}", "put",
            "SetPaymentAccountMethodOptionCapability", etag: true, idempotency: true);

        var names = typeof(AccountPaymentCapabilityView).GetProperties()
            .Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", names);
        Assert.DoesNotContain("SecretRefName", names);
        Assert.DoesNotContain("Config", names);
    }

    [Fact]
    public async Task MerchantPaymentSelfRead_uses_server_identity_and_exposes_get_only_contracts()
    {
        using var factory = new AdminTask4Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");
        var methodsPath = paths.GetProperty("/api/v1/payments/methods");
        var optionsPath = paths.GetProperty("/api/v1/payments/methods/{method}/options");

        var methods = methodsPath.GetProperty("get");
        var options = optionsPath.GetProperty("get");
        Assert.Equal("ListMyEffectivePaymentMethods", methods.GetProperty("operationId").GetString());
        Assert.Equal("ListMyEffectivePaymentOptions", options.GetProperty("operationId").GetString());
        Assert.False(methodsPath.TryGetProperty("put", out _));
        Assert.False(optionsPath.TryGetProperty("put", out _));
        Assert.True(Requires(methods, "MerchantUserSession"));
        Assert.True(Requires(options, "MerchantUserSession"));
        var parameters = options.GetProperty("parameters").EnumerateArray().Select(x =>
            x.GetProperty("name").GetString()).ToArray();
        Assert.Contains("provider", parameters);
        Assert.DoesNotContain("merchantId", parameters);
        Assert.DoesNotContain("userId", parameters);
    }

    [Fact]
    public async Task OpenApi_pins_tenant_originator_psp_and_routing_mutation_contracts()
    {
        using var factory = new AdminTask4Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");

        AssertOperation(paths, "/api/v1/merchants", "get", "ListMerchants");
        AssertMutation(paths, "/api/v1/merchants/{merchantId}", "put", "UpdateMerchant", etag: true, idempotency: true);
        AssertMutation(paths, "/api/v1/merchants/{merchantId}/suspend", "post", "SuspendMerchant", etag: true, idempotency: true);
        AssertMutation(paths, "/api/v1/merchants/{merchantId}/reactivate", "post", "ReactivateMerchant", etag: true, idempotency: true);

        AssertOperation(paths, "/api/v1/originators", "get", "ListOriginators");
        AssertOperation(paths, "/api/v1/originators/{originatorId}", "get", "GetOriginator");
        AssertResponseEtag(AssertOperation(paths, "/api/v1/originators", "post", "CreateOriginator"), "201");
        AssertMutation(paths, "/api/v1/originators/{originatorId}", "put", "UpdateOriginator", etag: true);
        AssertMutation(paths, "/api/v1/originators/{originatorId}/enable", "post", "EnableOriginator", etag: true);
        AssertMutation(paths, "/api/v1/originators/{originatorId}/disable", "post", "DisableOriginator", etag: true);
        AssertMutation(paths, "/api/v1/originators/{originatorId}", "delete", "DeleteOriginator", etag: true);

        AssertOperation(paths, "/api/v1/payments/psp-connections", "get", "ListPspConnections");
        AssertOperation(paths, "/api/v1/payments/psp-connections/{connectionId}", "get", "GetPspConnection");
        AssertMutation(paths, "/api/v1/payments/psp-connections", "post", "CreatePspConnection", idempotency: true);
        AssertMutation(paths, "/api/v1/payments/psp-connections/{connectionId}", "put", "UpdatePspConnection", etag: true, idempotency: true);
        AssertMutation(paths, "/api/v1/payments/psp-connections/{connectionId}/test", "post", "TestPspConnection", etag: true, idempotency: true);
        AssertMutation(paths, "/api/v1/payments/psp-connections/{connectionId}/credential-change-requests", "post", "RequestPspCredentialChange", etag: true, idempotency: true);

        AssertOperation(paths, "/api/v1/payments/routing-rulesets", "get", "ListRoutingRulesets");
        AssertOperation(paths, "/api/v1/payments/routing-rulesets/{rulesetId}", "get", "GetRoutingRuleset");
        AssertOperation(paths, "/api/v1/payments/routing-rulesets", "post", "CreateRoutingRulesetDraft");
        AssertMutation(paths, "/api/v1/payments/routing-rulesets/{rulesetId}", "put", "ReplaceRoutingRulesetDraft", etag: true);
        AssertMutation(paths, "/api/v1/payments/routing-rulesets/{rulesetId}", "delete", "DeleteRoutingRulesetDraft", etag: true);
        AssertMutation(paths, "/api/v1/payments/routing-rulesets/{rulesetId}/activation-requests", "post", "RequestRoutingActivation", etag: true, idempotency: true);
    }

    [Fact]
    public void Psp_response_contract_exposes_no_secret_reference_or_plaintext_fields()
    {
        var names = typeof(PspConnectionView).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("SecretRefName", names);
        Assert.DoesNotContain("Secret", names);
        Assert.DoesNotContain("Secrets", names);
        Assert.Contains("MaskedSecrets", names);
        Assert.Contains("Capabilities", names);
        Assert.Contains("HasPendingCredentialChange", names);
    }

    [Fact]
    public void Psp_config_accepts_only_bounded_non_secret_fields()
    {
        AdminPaymentsControlStore.ValidateConfig(Json("""
            {"accountId":"acct_123","card":true,"installment":false,
             "enabledSources":["card","promptpay"],"returnUrls":["https://merchant.example/result"]}
            """));

        Assert.Throws<InvalidRequestException>(() => AdminPaymentsControlStore.ValidateConfig(
            Json("""{"nested":{"secretKey":"must-never-persist"}}""")));
        Assert.Throws<InvalidRequestException>(() => AdminPaymentsControlStore.ValidateConfig(
            Json("""{"returnUrls":["http://merchant.example/result"]}""")));
    }

    [Fact]
    public void Psp_idempotency_fingerprint_changes_with_credential_intent_without_storing_plaintext()
    {
        var first = AdminPaymentsControlStore.SecretIntentFingerprint("credential-one");
        var replay = AdminPaymentsControlStore.SecretIntentFingerprint("credential-one");
        var changed = AdminPaymentsControlStore.SecretIntentFingerprint("credential-two");

        Assert.Equal(first, replay);
        Assert.NotEqual(first, changed);
        Assert.DoesNotContain("credential", first, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static void AssertMutation(
        JsonElement paths, string path, string method, string operationId,
        bool etag = false, bool idempotency = false)
    {
        var operation = AssertOperation(paths, path, method, operationId);
        if (etag)
            AssertRequiredHeader(operation, "If-Match");
        if (idempotency)
            AssertRequiredHeader(operation, "Idempotency-Key");
    }

    private static JsonElement AssertOperation(JsonElement paths, string path, string method, string operationId)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        return operation;
    }

    private static void AssertRequiredHeader(JsonElement operation, string name)
    {
        var header = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "header"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        Assert.True(header.GetProperty("required").GetBoolean());
    }

    private static void AssertResponseEtag(JsonElement operation, string status) =>
        Assert.True(operation.GetProperty("responses").GetProperty(status)
            .GetProperty("headers").TryGetProperty("ETag", out _));

    private static bool Requires(JsonElement operation, string scheme) =>
        operation.GetProperty("security").EnumerateArray()
            .Any(requirement => requirement.EnumerateObject().Any(x => x.Name == scheme));
}

file sealed class AdminTask4Factory : WebApplicationFactory<ApiHost::Program>
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
