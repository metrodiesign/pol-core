extern alias ApiHost;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

// REQ-8.3/8.4/8.6: a fail-closed guard for the admin-plane CORS path table (design.md §5), written and passing
// against the PRE-MOVE code. AdminCsrfFilter carries no queryable endpoint metadata (verified: enumerating
// Endpoint.Metadata for POST /api/v1/admins/merchants shows only IAuthorizeData/HttpMethodMetadata/etc — nothing
// for a plain AddEndpointFilter<T> registration), so the admin-plane predicate here is "requires the 'admin'
// authorization policy" — today that set is exactly the endpoints under the /admins group, which is also where
// AdminCsrfFilter is attached (one call site, Program.cs), so the two coincide. After task 8 moves
// ProvisionMerchant/GetMerchant to /api/v1/merchants without updating PolCorsPolicyProvider's path table, this
// test starts failing on those two endpoints specifically — that is the point.

file sealed class CorsGuardFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string AdminOrigin = "https://admin.example.com";
    public const string MerchantUserOrigin = "https://app.example.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            ["Cors:AllowedOrigins:0"] = MerchantUserOrigin,
            ["Cors:AdminOrigins:0"] = AdminOrigin,
        }));
    }
}

public sealed class AdminCorsGuardTests
{
    [Fact]
    public async Task Every_admin_policy_endpoint_resolves_to_the_admin_CORS_policy_for_its_own_route_template()
    {
        using var factory = new CorsGuardFactory();
        using var _ = factory.CreateClient(); // force full startup so endpoints are mapped

        var adminPolicy = factory.Services.GetRequiredService<IOptions<CorsOptions>>().Value.GetPolicy(CorsExtensions.AdminPolicyName);
        var provider = factory.Services.GetRequiredService<ICorsPolicyProvider>();

        var adminPlaneEndpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.OfType<IAuthorizeData>().Any(a => a.Policy == "admin"))
            .ToList();

        Assert.NotEmpty(adminPlaneEndpoints); // sanity: the admin surface actually exists in this build

        foreach (var endpoint in adminPlaneEndpoints)
        {
            var http = new DefaultHttpContext { Request = { Path = endpoint.RoutePattern.RawText! } };

            var resolved = await provider.GetPolicyAsync(http, policyName: null);

            Assert.True(ReferenceEquals(adminPolicy, resolved),
                $"'{endpoint.RoutePattern.RawText}' requires the \"admin\" authorization policy, but " +
                "PolCorsPolicyProvider does not route it to the admin CORS policy — the admin-plane path table " +
                "(CorsExtensions.cs) is out of sync with the route.");
        }
    }
}
