extern alias ApiHost;

using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

public sealed class AdminTask6ContractTests
{
    [Fact]
    public async Task OpenApi_pins_admin_commerce_routes_dual_audience_and_conditional_guards()
    {
        using var factory = new AdminTask6Factory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("paths");

        var products = AssertOperation(paths, "/api/v1/products/documents", "get", "ListAdminProductDocuments");
        AssertAdmin(products);
        AssertQuery(products, "merchantId", required: true);
        AssertQuery(products, "originatorId", required: true);

        var export = AssertOperation(paths, "/api/v1/orders/export", "get", "ExportOrders");
        AssertAdmin(export);
        AssertQuery(export, "from", required: true);
        AssertQuery(export, "to", required: true);
        AssertQuery(export, "merchantId", required: false);
        AssertQuery(export, "filters", required: false);
        AssertQuery(export, "sort", required: false);
        AssertQuery(export, "search", required: false);

        var createCart = AssertOperation(paths, "/api/v1/carts", "post", "CreateCart");
        AssertDual(createCart);
        AssertConditionalHeader(createCart, "Idempotency-Key");
        AssertQuery(createCart, "merchantId", required: true);
        AssertQuery(createCart, "originatorId", required: true);
        AssertDual(AssertOperation(paths, "/api/v1/carts/{cartId}", "get", "GetCart"));
        AssertResponseEtag(AssertOperation(paths, "/api/v1/carts/{cartId}", "get", "GetCart"), "200");

        AssertCommerceMutation(AssertOperation(paths,
            "/api/v1/carts/{cartId}/items", "post", "AddCartItem"), update: false);
        AssertCommerceMutation(AssertOperation(paths,
            "/api/v1/carts/{cartId}/items/{itemId}", "put", "SetCartItemQuantity"), update: true);
        AssertCommerceMutation(AssertOperation(paths,
            "/api/v1/carts/{cartId}/items/{itemId}", "delete", "RemoveCartItem"), update: true);
        AssertCommerceMutation(AssertOperation(paths,
            "/api/v1/carts/{cartId}/clear", "post", "ClearCart"), update: true);

        var orders = AssertOperation(paths, "/api/v1/orders", "get", "ListOrders");
        AssertDual(orders);
        AssertQuery(orders, "merchantId", required: false);
        AssertDual(AssertOperation(paths, "/api/v1/orders/{orderId}", "get", "GetOrderDetail"));
        AssertResponseEtag(AssertOperation(paths, "/api/v1/orders/{orderId}", "get", "GetOrderDetail"), "200");
        AssertAdminCreate(AssertOperation(paths, "/api/v1/orders", "post", "CreateOrderFromCart"));
        AssertCommerceMutation(AssertOperation(paths,
            "/api/v1/orders/{orderId}/cancel", "post", "CancelOrder"), update: true);
        AssertCommerceMutation(AssertOperation(paths,
            "/api/v1/orders/{orderId}/summary/resend", "post", "ResendOrderSummary"), update: true);

        var createSession = AssertOperation(paths, "/api/v1/payments/sessions", "post", "CreatePaymentSession");
        AssertAdminCreate(createSession);
        var oneOf = createSession.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("oneOf");
        Assert.Equal(2, oneOf.GetArrayLength());

        AssertCommerceMutation(AssertOperation(paths,
            "/api/v1/payments/sessions/{paymentSessionId}/redirect", "post", "StartPaymentRedirect"), update: true);
        AssertDual(AssertOperation(paths,
            "/api/v1/payments/sessions/{paymentSessionId}", "get", "GetPaymentSession"));
        AssertResponseEtag(AssertOperation(paths,
            "/api/v1/payments/sessions/{paymentSessionId}", "get", "GetPaymentSession"), "200");
    }

    private static void AssertAdminCreate(JsonElement operation)
    {
        AssertDual(operation);
        AssertConditionalHeader(operation, "Idempotency-Key");
    }

    private static void AssertAdminUpdate(JsonElement operation)
    {
        AssertDual(operation);
        AssertConditionalHeader(operation, "If-Match");
        AssertConditionalHeader(operation, "Idempotency-Key");
    }

    private static void AssertCommerceMutation(JsonElement operation, bool update)
    {
        if (update)
            AssertAdminUpdate(operation);
        else
            AssertAdminCreate(operation);
        AssertQuery(operation, "merchantId", required: true);
    }

    private static JsonElement AssertOperation(
        JsonElement paths, string path, string method, string operationId)
    {
        var operation = paths.GetProperty(path).GetProperty(method);
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        return operation;
    }

    private static void AssertConditionalHeader(JsonElement operation, string name)
    {
        var header = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "header"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        Assert.False(header.TryGetProperty("required", out var required) && required.GetBoolean());
    }

    private static void AssertQuery(JsonElement operation, string name, bool required)
    {
        var query = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "query"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.Ordinal));
        Assert.Equal(required, query.TryGetProperty("required", out var value) && value.GetBoolean());
    }

    private static void AssertResponseEtag(JsonElement operation, string status) =>
        Assert.True(operation.GetProperty("responses").GetProperty(status)
            .GetProperty("headers").TryGetProperty("ETag", out _));

    private static void AssertDual(JsonElement operation)
    {
        var schemes = Schemes(operation);
        Assert.Contains("AdminSession", schemes);
        Assert.Contains("MerchantUserSession", schemes);
    }

    private static void AssertAdmin(JsonElement operation) =>
        Assert.Equal(["AdminSession"], Schemes(operation));

    private static HashSet<string> Schemes(JsonElement operation) => operation.GetProperty("security")
        .EnumerateArray().SelectMany(x => x.EnumerateObject().Select(p => p.Name))
        .ToHashSet(StringComparer.Ordinal);
}

file sealed class AdminTask6Factory : WebApplicationFactory<ApiHost::Program>
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
