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
    [Fact]
    public async Task Admin_roles_get_declares_the_sfs_query_parameters()
    {
        using var factory = new SfsOpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var op = root.GetProperty("paths").GetProperty("/api/v1/admins/roles").GetProperty("get");
        var names = op.GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .ToHashSet();

        Assert.Contains("page", names);
        Assert.Contains("limit", names);
        Assert.Contains("filters", names);
        Assert.Contains("sort", names);
        Assert.Contains("search", names);
    }

    // admin-account-management REQ-7.6/F2: the admin directory list carries the SfsQueryParamsMarker, so its SFS
    // query parameters must appear in the OpenAPI document just like the roles list.
    [Fact]
    public async Task Admin_directory_get_declares_the_sfs_query_parameters()
    {
        using var factory = new SfsOpenApiFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        var op = root.GetProperty("paths").GetProperty("/api/v1/admins").GetProperty("get");
        var names = op.GetProperty("parameters").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .ToHashSet();

        Assert.Contains("page", names);
        Assert.Contains("limit", names);
        Assert.Contains("filters", names);
        Assert.Contains("sort", names);
        Assert.Contains("search", names);
    }
}
