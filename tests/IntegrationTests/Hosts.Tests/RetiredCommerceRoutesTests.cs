extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
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
        { "PUT", $"/api/v1/admins/{Guid.NewGuid()}/microsoft-identity" },
        // Legacy admin identity plane (admin.Users): the canonical surface is /accounts, /roles, /permissions,
        // /me and /agent-registrations.
        { "POST", "/api/v1/admins" },
        { "GET", "/api/v1/admins" },
        { "GET", "/api/v1/admins/me" },
        { "GET", $"/api/v1/admins/{Guid.NewGuid()}" },
        { "GET", $"/api/v1/admins/{Guid.NewGuid()}/effective-permissions" },
        { "POST", $"/api/v1/admins/{Guid.NewGuid()}/merchants" },
        { "DELETE", $"/api/v1/admins/{Guid.NewGuid()}/merchants/{Guid.NewGuid()}" },
        { "POST", $"/api/v1/admins/{Guid.NewGuid()}/suspend" },
        { "POST", $"/api/v1/admins/{Guid.NewGuid()}/reactivate" },
        { "POST", $"/api/v1/admins/{Guid.NewGuid()}/tier" },
        { "PUT", $"/api/v1/admins/{Guid.NewGuid()}/roles" },
        { "GET", "/api/v1/admins/permissions" },
        { "GET", "/api/v1/admins/roles" },
        { "POST", "/api/v1/admins/roles" },
        { "GET", "/api/v1/admins/roles/platform_admin" },
        { "PUT", "/api/v1/admins/roles/platform_admin" },
        { "DELETE", "/api/v1/admins/roles/platform_admin" },
        { "GET", $"/api/v1/admins/merchants/users/{Guid.NewGuid()}/registrations" },
        { "POST", $"/api/v1/admins/merchants/users/{Guid.NewGuid()}/approve" },
        { "POST", $"/api/v1/admins/merchants/users/{Guid.NewGuid()}/reject" },
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
        Assert.DoesNotContain(paths, path => path.Contains("/microsoft-identity", StringComparison.Ordinal));
        Assert.DoesNotContain(paths, path => path.StartsWith("/api/v1/admins", StringComparison.Ordinal));
    }
}
