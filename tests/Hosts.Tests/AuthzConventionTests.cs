extern alias ApiHost;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// microsoft-oidc-ciam-alignment REQ-6.5 (decision A6/R2): EVERY mapped endpoint must state its authorization
// posture EXPLICITLY — IAuthorizeData (RequireAuthorization/RequirePermission) or IAllowAnonymous — so a new
// endpoint can never ship silently unauthenticated. The baseline lists the endpoints that are legitimately
// metadata-free today, keyed (HTTP method, route pattern) via IHttpMethodMetadata (a path can host several
// methods with different postures); a NEW unlisted endpoint without metadata fails this test.

file sealed class AuthzConventionFactory : WebApplicationFactory<ApiHost::Program>
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

public sealed class AuthzConventionTests
{
    /// <summary>Endpoints allowed to carry NO authorization metadata, with the reason each is exempt.
    /// Additions require review — prefer an explicit .AllowAnonymous() on the endpoint instead.</summary>
    private static readonly Dictionary<(string Method, string Pattern), string> Baseline = new()
    {
        [("*", "/health/live")] = "infra liveness probe — anonymous by design, no business data",
        [("*", "/health/ready")] = "infra readiness probe — anonymous by design, no business data",
        [("GET", "/openapi/{documentName}.json")] = "Development-only OpenAPI document (not mapped outside Dev)",
        [("GET", "/scalar/{documentName?}")] = "Development-only Scalar UI (not mapped outside Dev)",
        [("POST", "/api/v1/webhooks/{pspConnectionId:guid}")] =
            "PSP webhook — authenticated by per-connection signature verification inside the handler, not by a session scheme",
    };

    [Fact]
    public void Every_mapped_endpoint_declares_authorization_or_allow_anonymous_or_is_baselined()
    {
        using var factory = new AuthzConventionFactory();
        using var _ = factory.CreateClient(); // force full startup so the endpoints are mapped

        var offenders = new List<string>();
        foreach (var endpoint in factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<IAuthorizeData>() is not null
                || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                continue;

            var pattern = endpoint.RoutePattern.RawText ?? "";
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"];
            foreach (var method in methods)
                if (!Baseline.ContainsKey((method, pattern)))
                    offenders.Add($"{method} {pattern}");
        }

        offenders.Sort(StringComparer.Ordinal);
        Assert.True(offenders.Count == 0,
            "Endpoints with NO authorization metadata (add RequireAuthorization/RequirePermission or an explicit "
            + "AllowAnonymous; baseline additions need review):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_baseline_lists_only_endpoints_that_still_exist_and_still_lack_metadata()
    {
        using var factory = new AuthzConventionFactory();
        using var _ = factory.CreateClient();

        var current = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(m => (Method: m, Pattern: e.RoutePattern.RawText ?? "")))
            .ToHashSet();

        var stale = Baseline.Keys.Where(k => !current.Contains(k)).Select(k => $"{k.Method} {k.Pattern}").ToList();
        Assert.True(stale.Count == 0,
            "Baseline entries whose endpoint is gone or now carries metadata — remove them:\n  "
            + string.Join("\n  ", stale));
    }
}
