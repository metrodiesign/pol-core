extern alias ApiHost;
using System.Text.Json;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

file sealed class OpenApiDocumentFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
    }
}

public sealed class AudienceOpenApiDocumentTests
{
    private static readonly HashSet<string> HttpMethods =
        ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    [Fact]
    public async Task Named_documents_partition_the_combined_contract_and_drive_Scalar()
    {
        using var factory = new OpenApiDocumentFactory();
        using var client = factory.CreateClient();

        var v1 = await DocumentAsync(client, "v1");
        var merchant = await DocumentAsync(client, "merchant");
        var admin = await DocumentAsync(client, "admin");
        var integration = await DocumentAsync(client, "integration");

        var v1Operations = Operations(v1);
        var merchantOperations = Operations(merchant);
        var adminOperations = Operations(admin);
        var integrationOperations = Operations(integration);
        var namedUnion = merchantOperations
            .Union(adminOperations, StringComparer.Ordinal)
            .Union(integrationOperations, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(v1Operations.SetEquals(namedUnion),
            "Named-document union differs from v1. Missing: "
            + string.Join(", ", v1Operations.Except(namedUnion).Order(StringComparer.Ordinal))
            + "; extra: "
            + string.Join(", ", namedUnion.Except(v1Operations).Order(StringComparer.Ordinal)));

        Assert.Contains("get /api/v1/products", merchantOperations);
        Assert.DoesNotContain("get /api/v1/products", adminOperations);
        Assert.Contains("get /api/v1/admins", adminOperations);
        Assert.DoesNotContain("get /api/v1/admins", merchantOperations);
        Assert.Contains("post /api/v1/carts", merchantOperations);
        Assert.Contains("post /api/v1/carts", adminOperations);
        Assert.Contains("get /api/v1/orders/{token}/summary", merchantOperations);
        Assert.Contains("get /api/v1/orders/{token}/summary", integrationOperations);
        Assert.Contains("post /api/v1/webhooks/{pspConnectionId}", integrationOperations);
        Assert.DoesNotContain(integrationOperations, operation =>
            operation.Contains("/admins", StringComparison.Ordinal)
            || operation.Contains("/merchants/users", StringComparison.Ordinal));

        Assert.Equal(["AdminSession", "MerchantUserSession"], SecuritySchemes(v1));
        Assert.Equal(["MerchantUserSession"], SecuritySchemes(merchant));
        Assert.Equal(["AdminSession"], SecuritySchemes(admin));
        Assert.Empty(SecuritySchemes(integration));
        Assert.Equal(["MerchantUserSession"],
            OperationSecuritySchemes(merchant, "/api/v1/carts", "post"));
        Assert.Equal(["AdminSession"], OperationSecuritySchemes(admin, "/api/v1/carts", "post"));
        Assert.Empty(OperationSecuritySchemes(integration, "/api/v1/orders/{token}/summary", "get"));

        var combinedRequest = RequestSchema(v1, "/api/v1/payments/sessions", "post");
        Assert.Equal(2, combinedRequest.GetProperty("oneOf").GetArrayLength());
        var merchantRequest = RequestSchema(merchant, "/api/v1/payments/sessions", "post");
        Assert.False(merchantRequest.TryGetProperty("oneOf", out _));
        Assert.True(merchantRequest.GetProperty("properties").TryGetProperty("psp", out _));
        Assert.False(merchantRequest.GetProperty("properties").TryGetProperty("merchantId", out _));
        var adminRequest = RequestSchema(admin, "/api/v1/payments/sessions", "post");
        Assert.False(adminRequest.TryGetProperty("oneOf", out _));
        Assert.True(adminRequest.GetProperty("properties").TryGetProperty("merchantId", out _));
        Assert.False(adminRequest.GetProperty("properties").TryGetProperty("psp", out _));

        var combinedResponse = ResponseSchema(
            v1, "/api/v1/payments/sessions/{paymentSessionId}", "get", "200");
        Assert.Equal(2, combinedResponse.GetProperty("oneOf").GetArrayLength());
        var merchantResponse = ResponseSchema(
            merchant, "/api/v1/payments/sessions/{paymentSessionId}", "get", "200");
        Assert.False(merchantResponse.TryGetProperty("oneOf", out _));
        Assert.True(merchantResponse.GetProperty("properties").TryGetProperty("pspExternalChargeId", out _));
        Assert.False(merchantResponse.GetProperty("properties").TryGetProperty("version", out _));
        var adminResponse = ResponseSchema(
            admin, "/api/v1/payments/sessions/{paymentSessionId}", "get", "200");
        Assert.False(adminResponse.TryGetProperty("oneOf", out _));
        Assert.True(adminResponse.GetProperty("properties").TryGetProperty("version", out _));
        Assert.False(adminResponse.GetProperty("properties").TryGetProperty("pspExternalChargeId", out _));

        foreach (var document in new[] { v1, merchant, admin, integration })
        {
            AssertTagGroupsCoverActiveTagsExactlyOnce(document);
            AssertEveryOperationIsDocumented(document);
        }

        Assert.Contains("/api/v1/admins/auth/{provider}/login",
            SecuritySchemeDescription(admin, "AdminSession"), StringComparison.Ordinal);
        Assert.Contains("/api/v1/merchants/auth/{provider}/login",
            SecuritySchemeDescription(merchant, "MerchantUserSession"), StringComparison.Ordinal);
        var publishedText = v1.GetRawText();
        Assert.DoesNotContain("/api/v1/admins/auth/login", publishedText, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1/merchants/users/auth/login", publishedText, StringComparison.Ordinal);
        Assert.DoesNotMatch("\\bT[0-9]+\\b", publishedText);
        Assert.DoesNotContain("Bearer", publishedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("merchant-user.approve", publishedText, StringComparison.Ordinal);
        Assert.DoesNotContain("merchant-user.reject", publishedText, StringComparison.Ordinal);
        AssertAudienceDescription(v1, "/api/v1/payments/sessions", "post");
        AssertAudienceDescription(v1, "/api/v1/payments/sessions/{paymentSessionId}/redirect", "post");
        AssertAudienceDescription(v1, "/api/v1/payments/sessions/{paymentSessionId}", "get");

        var scalar = await client.GetStringAsync("/scalar");
        Assert.Contains("Merchant API", scalar, StringComparison.Ordinal);
        Assert.Contains("Admin API", scalar, StringComparison.Ordinal);
        Assert.Contains("Integration API", scalar, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"openapi/merchant.json\",\"default\":true", scalar, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"openapi/admin.json\"", scalar, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"openapi/integration.json\"", scalar, StringComparison.Ordinal);
        Assert.DoesNotContain("\"url\":\"openapi/v1.json\"", scalar, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> DocumentAsync(HttpClient client, string name)
    {
        using var response = await client.GetAsync($"/openapi/{name}.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static HashSet<string> Operations(JsonElement document) =>
        document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(operation => HttpMethods.Contains(operation.Name))
                .Select(operation => $"{operation.Name} {path.Name}"))
            .ToHashSet(StringComparer.Ordinal);

    private static string[] SecuritySchemes(JsonElement document)
    {
        if (!document.TryGetProperty("components", out var components)
            || !components.TryGetProperty("securitySchemes", out var schemes))
            return [];
        return [.. schemes.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal)];
    }

    private static string SecuritySchemeDescription(JsonElement document, string scheme) =>
        document.GetProperty("components").GetProperty("securitySchemes").GetProperty(scheme)
            .GetProperty("description").GetString()!;

    private static string[] OperationSecuritySchemes(
        JsonElement document, string path, string method)
    {
        var operation = document.GetProperty("paths").GetProperty(path).GetProperty(method);
        if (!operation.TryGetProperty("security", out var security))
            return [];
        return [.. security.EnumerateArray()
            .SelectMany(requirement => requirement.EnumerateObject().Select(x => x.Name))
            .Order(StringComparer.Ordinal)];
    }

    private static JsonElement RequestSchema(JsonElement document, string path, string method) =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("requestBody").GetProperty("content").EnumerateObject().First().Value
            .GetProperty("schema");

    private static JsonElement ResponseSchema(
        JsonElement document, string path, string method, string status) =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("responses").GetProperty(status).GetProperty("content")
            .EnumerateObject().First().Value.GetProperty("schema");

    private static void AssertAudienceDescription(
        JsonElement document, string path, string method)
    {
        var description = document.GetProperty("paths").GetProperty(path).GetProperty(method)
            .GetProperty("description").GetString();
        Assert.Contains("Merchant Console", description, StringComparison.Ordinal);
        Assert.Contains("Admin Console", description, StringComparison.Ordinal);
    }

    private static void AssertTagGroupsCoverActiveTagsExactlyOnce(JsonElement document)
    {
        var active = document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Where(operation => HttpMethods.Contains(operation.Name))
            .SelectMany(operation => operation.Value.GetProperty("tags").EnumerateArray())
            .Select(tag => tag.GetString()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var grouped = document.GetProperty("x-tagGroups").EnumerateArray()
            .SelectMany(group => group.GetProperty("tags").EnumerateArray())
            .Select(tag => tag.GetString()!)
            .ToArray();

        Assert.Equal(grouped.Length, grouped.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(active, grouped.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AssertEveryOperationIsDocumented(JsonElement document)
    {
        var missing = new List<string>();
        foreach (var path in document.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject()
                         .Where(x => HttpMethods.Contains(x.Name)))
            {
                if (!operation.Value.TryGetProperty("summary", out var summary)
                    || string.IsNullOrWhiteSpace(summary.GetString()))
                    missing.Add($"summary: {operation.Name} {path.Name}");
                if (!operation.Value.TryGetProperty("description", out var description)
                    || string.IsNullOrWhiteSpace(description.GetString()))
                    missing.Add($"description: {operation.Name} {path.Name}");
            }
        Assert.Empty(missing);
    }
}
