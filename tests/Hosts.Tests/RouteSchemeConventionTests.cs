extern alias ApiHost;
using System.Net;
using System.Text.RegularExpressions;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hosts.Tests;

// api-route-scheme REQ-1.5/4.3/5.3: the architecture guard for the /api/v1/{area} scheme. Boots the real host and
// (1) enumerates EndpointDataSource, asserting every RouteEndpoint sits under the LITERAL /api/v1/{area} or the
// infra allowlist (fail-closed on v1 — a silent /api/v2/... must NOT pass), and (2) confirms every legacy method+path
// from the mapping table (all 47) plus the two old OIDC callback paths now 404. No live DB: the routing table is
// resolved from DI and an unmatched route 404s before any handler runs.

file sealed class RouteSchemeFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development); // MapOpenApi + MapScalarApiReference are Dev-only (infra allowlist)
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

public sealed class RouteSchemeConventionTests
{
    // REQ-1.5: LITERAL /api/v1 (fail-closed) — NOT v\d+. The nine areas are the REQ-2.1 taxonomy (hierarchical-naming
    // task 8: merchant-users out, merchants in — it now covers both provisioning and the merchant-user surface).
    private static readonly Regex ApiScheme = new(
        @"^/api/v1/(products|carts|checkouts|orders|payments|admins|merchants|webhooks|reports)(/.*)?$",
        RegexOptions.Compiled);

    // REQ-4.3: health/readiness + the OpenAPI document + the Scalar UI are excluded from the area taxonomy.
    private static bool IsInfra(string raw) =>
        raw is "/health/live" or "/health/ready"
        || raw.StartsWith("/openapi/", StringComparison.Ordinal)
        || raw.StartsWith("/scalar", StringComparison.Ordinal);

    [Fact]
    public void Every_routed_endpoint_is_under_api_v1_area_or_the_infra_allowlist()
    {
        using var factory = new RouteSchemeFactory();
        using var _ = factory.CreateClient(); // force full startup so the endpoints are mapped

        var offenders = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .Where(raw => !ApiScheme.IsMatch(raw) && !IsInfra(raw))
            .Distinct()
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Routes outside /api/v1/{area} or the infra allowlist:\n  " + string.Join("\n  ", offenders));
    }

    // REQ-5.3: every old method+path (the full mapping table — 47 routes) + the two old OIDC callback paths. Concrete
    // ids are placeholders: the routes are GONE, so the shape never matters — any request to them must 404.
    private const string G = "11111111-1111-1111-1111-111111111111";
    private static readonly (string Method, string Path)[] LegacyRoutes =
    [
        // data-plane (16)
        ("POST", "/products"), ("GET", "/products"),
        ("POST", "/carts"), ("POST", $"/carts/{G}/items"), ("GET", $"/carts/{G}"),
        ("DELETE", $"/carts/{G}/items/{G}"), ("PUT", $"/carts/{G}/items/{G}"), ("POST", $"/carts/{G}/clear"),
        ("POST", "/checkout"), ("POST", $"/checkout/{G}/confirm"),
        ("POST", "/payment-sessions"), ("POST", $"/payment-sessions/{G}/redirect"),
        ("GET", "/orders/tok/summary"), ("POST", $"/orders/{G}/summary/resend"),
        ("GET", "/reports/reconciliation"),
        ("POST", $"/webhooks/{G}"),
        // admins (19)
        ("GET", "/admin/auth/login"), ("POST", "/admin/auth/logout"), ("POST", "/admin/auth/logout-all"),
        ("POST", "/admin/tenants"), ("GET", "/admin/tenants/acme"),
        ("POST", "/admin/tenant-users/sub-123/approve"), ("POST", "/admin/tenant-users/sub-123/reject"),
        ("GET", "/admin/me"), ("POST", "/admin/admins"),
        ("POST", $"/admin/admins/{G}/tenants"), ("DELETE", $"/admin/admins/{G}/tenants/{G}"),
        ("POST", $"/admin/admins/{G}/suspend"), ("PUT", $"/admin/admins/{G}/roles"),
        ("GET", "/admin/permissions"), ("GET", "/admin/roles"), ("GET", "/admin/roles/super_admin"),
        ("POST", "/admin/roles"), ("PUT", "/admin/roles/super_admin"), ("DELETE", "/admin/roles/super_admin"),
        // producers (12)
        ("GET", "/producer/auth/login"), ("POST", "/producer/register"),
        ("POST", "/producer/auth/logout"), ("POST", "/producer/auth/logout-all"),
        ("GET", "/producer/me"), ("GET", "/producer/permissions"),
        ("GET", "/producer/roles"), ("GET", "/producer/roles/tenant_owner"), ("POST", "/producer/roles"),
        ("PUT", "/producer/roles/tenant_owner"), ("DELETE", "/producer/roles/tenant_owner"),
        ("PUT", $"/producer/tenant-users/{G}/roles"),
        // OIDC callbacks — middleware, moved via CallbackPath (REQ-6.2); the old paths no longer intercept -> 404
        ("GET", "/admin/auth/callback"), ("GET", "/producer/auth/callback"),
    ];

    [Fact]
    public async Task Every_legacy_path_returns_404_after_cutover()
    {
        using var factory = new RouteSchemeFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var survivors = new List<string>();
        foreach (var (method, path) in LegacyRoutes)
        {
            using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            if (response.StatusCode != HttpStatusCode.NotFound)
                survivors.Add($"{method} {path} -> {(int)response.StatusCode}");
        }

        Assert.True(survivors.Count == 0,
            "Legacy paths that did not 404 after cutover:\n  " + string.Join("\n  ", survivors));
    }
}
