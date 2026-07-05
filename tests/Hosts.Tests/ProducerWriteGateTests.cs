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

// producer-google-sso REQ-17.4: the 3 write endpoints are gated behind Producer:EnforcePermissionsOnWrites. ON ->
// the "producer" policy + RequireProducerPermission(...); OFF -> the pre-existing "tenant" policy, un-gated. This
// inspects the booted endpoint metadata (no DB, no auth) so the flag's effect on each endpoint is proven directly.

file sealed class GateFactory(bool enforce) : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Producer"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["Producer:EnforcePermissionsOnWrites"] = enforce ? "true" : "false",
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

public sealed class ProducerWriteGateTests
{
    private static readonly (string Route, string Permission)[] Endpoints =
    [
        ("/api/v1/products", "product.create"),
        ("/api/v1/payments/sessions", "payment.create"),
        ("/api/v1/payments/sessions/{paymentSessionId:guid}/redirect", "payment.redirect"),
    ];

    [Fact]
    public void Flag_off_keeps_the_tenant_policy_and_no_producer_permission_gate()
    {
        using var factory = new GateFactory(enforce: false);
        using var _ = factory.CreateClient(); // force full startup so the endpoints are mapped
        foreach (var (route, _2) in Endpoints)
        {
            var endpoint = FindPost(factory, route);
            Assert.Equal("tenant", PolicyOf(endpoint));
            Assert.DoesNotContain(endpoint.Metadata, m => m is ApiHost::Api.RequiredProducerPermission);
        }
    }

    [Fact]
    public void Flag_on_applies_the_producer_policy_and_the_matching_permission_gate()
    {
        using var factory = new GateFactory(enforce: true);
        using var _ = factory.CreateClient();
        foreach (var (route, permission) in Endpoints)
        {
            var endpoint = FindPost(factory, route);
            Assert.Equal("producer", PolicyOf(endpoint));
            var perm = Assert.Single(endpoint.Metadata.OfType<ApiHost::Api.RequiredProducerPermission>());
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
