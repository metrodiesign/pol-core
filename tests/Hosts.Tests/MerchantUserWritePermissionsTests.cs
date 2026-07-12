extern alias ApiHost;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// rf1-schema-reset T11: the merchant-Bearer fallback is retired, so the former Producer:EnforcePermissionsOnWrites
// toggle (transitional un-gated Bearer state) is gone — every write endpoint gates on the single-scheme
// "merchant-user" policy + its permission UNCONDITIONALLY now. This inspects the booted endpoint metadata (no DB,
// no auth) so a regression that drops the policy or the permission gate on any of the 3 write endpoints is caught.

file sealed class GateFactory : WebApplicationFactory<ApiHost::Program>
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
        builder.ConfigureServices(services =>
        {
            var dispatcher = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxDispatcher));
            if (dispatcher is not null)
                services.Remove(dispatcher);
        });
    }
}

public sealed class MerchantUserWritePermissionsTests
{
    private static readonly (string Route, string Permission)[] Endpoints =
    [
        ("/api/v1/products", "product.create"),
        ("/api/v1/payments/sessions", "payment.create"),
        ("/api/v1/payments/sessions/{paymentSessionId:guid}/redirect", "payment.redirect"),
    ];

    [Fact]
    public void Every_write_endpoint_is_gated_on_the_merchant_user_policy_and_its_permission()
    {
        using var factory = new GateFactory();
        using var _ = factory.CreateClient(); // force full startup so the endpoints are mapped
        foreach (var (route, permission) in Endpoints)
        {
            var endpoint = FindPost(factory, route);
            Assert.Equal("merchant-user", PolicyOf(endpoint));
            var perm = Assert.Single(endpoint.Metadata.OfType<ApiHost::Api.Merchants.RequiredUserPermission>());
            Assert.Equal(permission, perm.Permission);
        }
    }

    private static RouteEndpoint FindPost(WebApplicationFactory<ApiHost::Program> factory, string route) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == route
                && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") ?? false));

    private static string? PolicyOf(Endpoint endpoint) =>
        endpoint.Metadata.OfType<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => !string.IsNullOrEmpty(p));
}
