extern alias ApiHost;
using System.Text.Json;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// SFS endpoints read page/limit/filters/sort/search from the raw query string, so ASP.NET emits no OpenAPI
// parameters for them; an operation transformer adds them wherever the SfsQueryParamsMarker is present. This
// boots the real OpenAPI document (Development, where MapOpenApi serves /openapi/v1.json) and asserts the SFS
// parameters are declared on GET /api/v1/admins/roles (REQ-13).
file sealed class SfsOpenApiFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
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

public sealed class SfsOpenApiTests
{
    private static async Task<JsonElement> OpenApiAsync()
    {
        using var factory = new SfsOpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<HashSet<string?>> QueryParameterNamesAsync(string path)
    {
        using var factory = new SfsOpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var op = root.GetProperty("paths").GetProperty(path).GetProperty("get");
        return [.. op.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString())];
    }

    private static async Task<JsonElement> QueryParameterAsync(string path, string name)
    {
        using var factory = new SfsOpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var op = root.GetProperty("paths").GetProperty(path).GetProperty("get");
        return op.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == name).Clone();
    }

    // REQ-4.8: saleCode moved server-side and is no longer a member of productFilters, so the published contract
    // must not advertise it — an SDK generator that read a mandatory saleCode would hand every client an argument
    // the server silently ignores, the exact trust-boundary inversion REQ-4.8 forbids. REQ-3.2: exactly one
    // catalogue side (Motor|NonMotor) must be named, so productFilters is a required query parameter — the OpenAPI
    // flag must match that behaviour, not the earlier "optional" gloss.
    [Fact]
    public async Task Products_get_productFilters_is_required_and_does_not_advertise_saleCode()
    {
        var productFilters = await QueryParameterAsync("/api/v1/products", "productFilters");

        Assert.True(productFilters.GetProperty("required").GetBoolean());
        Assert.DoesNotContain("saleCode", productFilters.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    // Every active endpoint carrying SfsQueryParamsMarker must declare all five SFS parameters.
    [Theory]
    [InlineData("/api/v1/admins/roles")]
    [InlineData("/api/v1/admins")]
    [InlineData("/api/v1/orders")]
    [InlineData("/api/v1/payments/sessions")]
    public async Task An_sfs_endpoint_declares_the_sfs_query_parameters(string path)
    {
        var names = await QueryParameterNamesAsync(path);

        Assert.Contains("page", names);
        Assert.Contains("limit", names);
        Assert.Contains("filters", names);
        Assert.Contains("sort", names);
        Assert.Contains("search", names);
    }

    [Fact]
    public async Task Money_product_and_checkout_schemas_publish_real_wire_contracts()
    {
        var root = await OpenApiAsync();
        var schemas = root.GetProperty("components").GetProperty("schemas");

        var money = schemas.GetProperty("Money");
        Assert.Equal("object", money.GetProperty("type").GetString());
        var amount = money.GetProperty("properties").GetProperty("amount");
        Assert.Equal("string", amount.GetProperty("type").GetString());
        Assert.Equal(@"^\d+\.\d{4}$", amount.GetProperty("pattern").GetString());
        Assert.Equal("string", money.GetProperty("properties").GetProperty("currency").GetProperty("type").GetString());

        var product = schemas.GetProperty("ProductListItem").GetProperty("properties");
        Assert.True(product.TryGetProperty("productCode", out _));
        Assert.True(product.TryGetProperty("variantCode", out _));

        var productSchema = schemas.GetProperty("ProductListItem");
        var productRequired = productSchema.GetProperty("required").EnumerateArray()
            .Select(x => x.GetString()).ToHashSet();
        Assert.Contains("productCode", productRequired);
        Assert.Contains("variantCode", productRequired);
        Assert.Equal("string", product.GetProperty("productCode").GetProperty("type").GetString());
        Assert.Equal("string", product.GetProperty("variantCode").GetProperty("type").GetString());

        foreach (var paged in schemas.EnumerateObject().Where(x => x.Name.StartsWith("PagedResultOf", StringComparison.Ordinal)))
        {
            var required = paged.Value.GetProperty("required").EnumerateArray()
                .Select(x => x.GetString()).ToHashSet();
            Assert.Contains("totalPages", required);
            Assert.Equal("integer", paged.Value.GetProperty("properties")
                .GetProperty("totalPages").GetProperty("type").GetString());
        }

        var checkout = schemas.GetProperty("CreateOrderFromCartRequest").GetProperty("properties");
        Assert.True(checkout.TryGetProperty("paymentMethod", out _));
        Assert.False(checkout.TryGetProperty("amount", out _));
    }

    [Fact]
    public async Task Registration_multipart_schema_uses_producerCode_and_requires_photo()
    {
        var root = await OpenApiAsync();
        var operation = root.GetProperty("paths").GetProperty("/api/v1/merchants/users/register").GetProperty("post");
        var schema = operation.GetProperty("requestBody").GetProperty("content")
            .GetProperty("multipart/form-data").GetProperty("schema");
        var reference = schema.GetProperty("$ref").GetString()!;
        var name = reference[(reference.LastIndexOf('/') + 1)..];
        var request = root.GetProperty("components").GetProperty("schemas").GetProperty(name);
        var properties = request.GetProperty("properties");
        var required = request.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToHashSet();

        Assert.True(properties.TryGetProperty("producerCode", out _));
        Assert.False(properties.TryGetProperty("saleCode", out _));
        Assert.True(properties.TryGetProperty("photo", out var photo));
        var photoReference = photo.GetProperty("$ref").GetString()!;
        var photoSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty(photoReference[(photoReference.LastIndexOf('/') + 1)..]);
        Assert.Equal("string", photoSchema.GetProperty("type").GetString());
        Assert.Equal("binary", photoSchema.GetProperty("format").GetString());
        Assert.Contains("producerCode", required);
        Assert.Contains("photo", required);
    }

    // REQ-7.4: the document list left the SFS surface — it declares page/limit/productFilters and must not
    // advertise filters/sort/search, which it no longer reads.
    [Fact]
    public async Task Products_get_declares_only_the_paging_and_productFilters_parameters()
    {
        var names = await QueryParameterNamesAsync("/api/v1/products");

        Assert.Equal(["limit", "page", "productFilters"], names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.DoesNotContain("filters", names);
        Assert.DoesNotContain("sort", names);
        Assert.DoesNotContain("search", names);
    }

    [Fact]
    public async Task Published_cart_and_order_contracts_match_the_big_bang_cutover()
    {
        var root = await OpenApiAsync();
        var paths = root.GetProperty("paths");
        var schemas = root.GetProperty("components").GetProperty("schemas");

        var addItem = paths.GetProperty("/api/v1/carts/{cartId}/items").GetProperty("post");
        Assert.Equal("#/components/schemas/AddItemToCartRequest",
            addItem.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
        var addProperties = schemas.GetProperty("AddItemToCartRequest").GetProperty("properties");
        Assert.True(addProperties.TryGetProperty("productCode", out _));
        Assert.True(addProperties.TryGetProperty("variantCode", out _));
        Assert.True(addProperties.TryGetProperty("quantity", out _));
        Assert.False(addProperties.TryGetProperty("unitPrice", out _));
        Assert.False(addProperties.TryGetProperty("metadata", out _));

        var createOrder = paths.GetProperty("/api/v1/orders").GetProperty("post");
        Assert.Equal("#/components/schemas/CreateOrderFromCartRequest",
            createOrder.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/DirectOrderResult",
            createOrder.GetProperty("responses").GetProperty("201").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        foreach (var status in new[] { "400", "403", "404", "409", "503" })
            Assert.True(createOrder.GetProperty("responses").TryGetProperty(status, out _), status);

        var createOrderProperties = schemas.GetProperty("CreateOrderFromCartRequest").GetProperty("properties");
        Assert.True(createOrderProperties.TryGetProperty("paymentMethod", out _));
        Assert.False(createOrderProperties.TryGetProperty("amount", out _));

        Assert.DoesNotContain(paths.EnumerateObject(), path =>
            path.Name.Contains("checkout", StringComparison.OrdinalIgnoreCase)
            || path.Name.Contains("policy", StringComparison.OrdinalIgnoreCase));
    }
}
