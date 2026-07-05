extern alias ApiHost;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// api-route-scheme REQ-3.4/3.5: the area-prefix move carries ZERO authorization change. Read straight off the booted
// endpoint metadata (no DB): the products READ stays tenant-gated at its new path (the WRITE split is proven by
// ProducerWriteGateTests), and the producer register stays AllowAnonymous — its move must NOT newly require a session.
// (admins-area rejects non-admin -> AdminProvisioningAuthorizationTests; anon login reachable -> the login-redirect tests.)

file sealed class AuthPreservationFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Producer"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
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

public sealed class RouteSchemeAuthPreservationTests
{
    private static RouteEndpoint Endpoint(WebApplicationFactory<ApiHost::Program> factory, string rawText, string method) =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == rawText
                && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) ?? false));

    [Fact]
    public void Products_read_still_requires_the_tenant_policy_at_the_new_path()
    {
        using var factory = new AuthPreservationFactory();
        using var _ = factory.CreateClient(); // force full startup so the endpoints are mapped

        var get = Endpoint(factory, "/api/v1/products", "GET");
        var policy = get.Metadata.OfType<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => !string.IsNullOrEmpty(p));

        Assert.Equal("tenant", policy); // read stays tenant-gated (REQ-3.5)
    }

    [Fact]
    public void Producer_register_stays_anonymous_at_the_new_path()
    {
        using var factory = new AuthPreservationFactory();
        using var _ = factory.CreateClient();

        var register = Endpoint(factory, "/api/v1/producers/register", "POST");

        Assert.Contains(register.Metadata, m => m is IAllowAnonymous); // anon-reachable, unchanged by the move (REQ-3.4)
    }
}
