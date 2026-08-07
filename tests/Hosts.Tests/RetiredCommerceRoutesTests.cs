extern alias ApiHost;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

file sealed class RetiredCommerceFactory : WebApplicationFactory<ApiHost::Program>
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

public sealed class RetiredCommerceRoutesTests
{
    public static TheoryData<string, string> RetiredRoutes => new()
    {
        { "POST", "/api/v1/checkouts" },
        { "POST", $"/api/v1/checkouts/{Guid.NewGuid()}/confirm" },
        { "POST", $"/api/v1/checkouts/{Guid.NewGuid()}/abandon" },
        { "PUT", $"/api/v1/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/policy" },
        { "GET", "/api/v1/reports/policies" },
        { "PUT", $"/api/v1/admins/orders/{Guid.NewGuid()}/items/{Guid.NewGuid()}/policy" },
        { "GET", "/api/v1/admins/reports/policies" },
    };

    [Theory]
    [MemberData(nameof(RetiredRoutes))]
    public async Task Retired_route_returns_404(string method, string path)
    {
        using var factory = new RetiredCommerceFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_has_no_checkout_or_policy_operations()
    {
        using var factory = new RetiredCommerceFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths").EnumerateObject().Select(path => path.Name).ToList();

        Assert.DoesNotContain(paths, path => path.Contains("/checkouts", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.Contains("/reports/policies", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.EndsWith("/policy", StringComparison.Ordinal));
    }
}
