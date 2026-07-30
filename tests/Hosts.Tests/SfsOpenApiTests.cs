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
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
    }
}

public sealed class SfsOpenApiTests
{
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

    // Every endpoint carrying the SfsQueryParamsMarker must declare all five SFS parameters (REQ-13;
    // admin-account-management REQ-7.6/F2 for the admin directory). These are the four in the host today.
    [Theory]
    [InlineData("/api/v1/admins/roles")]
    [InlineData("/api/v1/admins")]
    [InlineData("/api/v1/reports/policies")]
    [InlineData("/api/v1/admins/reports/policies")]
    public async Task An_sfs_endpoint_declares_the_sfs_query_parameters(string path)
    {
        var names = await QueryParameterNamesAsync(path);

        Assert.Contains("page", names);
        Assert.Contains("limit", names);
        Assert.Contains("filters", names);
        Assert.Contains("sort", names);
        Assert.Contains("search", names);
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
}
