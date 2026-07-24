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

// rf2-iam-rbac REQ-10.4: pins every RequirePermission gate site's (route, method) -> (key, policy) so a rename,
// a dropped gate, or the user.roles<->users.roles swap (two near-identical literals now living in ONE catalog,
// REQ-10.4's specific worry) is caught at test time rather than in production. Supersedes the old narrower
// MerchantUserWritePermissionsTests (3 of the 7 merchant-user sites only).
//
// 26 gate SITES in source (REQ-4.5: admin 16 + merchant-user 8, +1 policy-reference-record merchant write +1
// policy-reference-record admin write from task 5, +1 policy-reference-record merchant report read +1
// policy-reference-record admin report read from task 6) map to more than 26 physical ROUTES at runtime
// because the reference master-data CRUD (positions/offices/levels/divisions, standalone areas since
// 2026-07-20) instantiates the SAME generic MapMasterCrud<TStore, TItem> body four times, now 5 verbs each
// (List/Get/Create/Update/Deactivate). This inventory pins one representative segment ("positions") for that
// generic body — the other three segments are the identical generic instantiation, not independent gate sites
// — landing back on exactly 26 pinned endpoints (10 + 16). PermissionParityTests.RealGateSites is the
// source-level completeness check (14 distinct (key, policy) pairs covering all call sites incl. duplicates).

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
    }
}

public sealed class PermissionGateSitesTests
{
    public sealed record Site(string Method, string Route, string Policy, string Key);

    private static readonly Site[] Sites =
    [
        // --- merchant-user (7) ---
        new("POST", "/api/v1/products", "merchant-user", "product.create"),
        new("POST", "/api/v1/payments/sessions", "merchant-user", "payment.create"),
        new("POST", "/api/v1/payments/sessions/{paymentSessionId:guid}/redirect", "merchant-user", "payment.redirect"),
        new("POST", "/api/v1/merchants/users/roles", "merchant-user", "roles.manage"),
        new("PUT", "/api/v1/merchants/users/roles/{code}", "merchant-user", "roles.manage"),
        new("DELETE", "/api/v1/merchants/users/roles/{code}", "merchant-user", "roles.manage"),
        new("PUT", "/api/v1/merchants/users/{merchantUserId:guid}/roles", "merchant-user", "users.roles"),
        new("PUT", "/api/v1/orders/{orderId:guid}/items/{itemId:guid}/policy", "merchant-user", "policies.write"),
        new("GET", "/api/v1/reports/policies", "merchant-user", "policies.read"),

        // --- admin (16) ---
        new("POST", "/api/v1/admins/merchants/users/{subject}/approve", "admin", "merchants.users.approve"),
        new("POST", "/api/v1/admins/merchants/users/{subject}/reject", "admin", "merchants.users.reject"),
        new("GET", "/api/v1/admins", "admin", "user.view"),
        new("GET", "/api/v1/admins/{id:guid}", "admin", "user.view"),
        new("GET", "/api/v1/admins/{id:guid}/effective-permissions", "admin", "user.view"),
        new("PUT", "/api/v1/admins/{id:guid}/profile", "admin", "user.manage"),
        new("GET", "/api/v1/positions", "admin", "user.manage"),
        new("GET", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/positions", "admin", "user.manage"),
        new("PUT", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/admins/roles", "admin", "user.roles"),
        new("PUT", "/api/v1/admins/roles/{code}", "admin", "user.roles"),
        new("DELETE", "/api/v1/admins/roles/{code}", "admin", "user.roles"),
        new("PUT", "/api/v1/admins/{id:guid}/roles", "admin", "user.roles"),
        new("PUT", "/api/v1/admins/orders/{orderId:guid}/items/{itemId:guid}/policy", "admin", "merchants.policies.write"),
        new("GET", "/api/v1/admins/reports/policies", "admin", "merchants.policies.read"),
    ];

    public static IEnumerable<object[]> SiteCases() => Sites.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(SiteCases))]
    public void Gate_site_carries_the_expected_key_and_policy(Site site)
    {
        using var factory = new GateFactory();
        using var _ = factory.CreateClient(); // force full startup so the endpoints are mapped

        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == site.Route
                && (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(site.Method) ?? false));

        var policy = endpoint.Metadata.OfType<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => !string.IsNullOrEmpty(p));
        Assert.Equal(site.Policy, policy);

        var required = Assert.Single(endpoint.Metadata.OfType<ApiHost::Api.Iam.RequiredPermission>());
        Assert.Equal(site.Key, required.Permission);
    }

    [Fact]
    public void Exactly_26_gate_sites_are_pinned() => Assert.Equal(26, Sites.Length); // REQ-4.5 count drift guard, +2 policy-reference-record write (task 5) +2 policy-reference-record report read (task 6)

    // REQ-10.3: the scheme ids themselves — a rename here would be a breaking contract change for both SPAs.
    [Fact]
    public void Auth_policy_scheme_mapping_pins_the_literal_scheme_ids()
    {
        Assert.Equal("AdminSession", ApiHost::Api.Iam.AuthPolicyScheme.For("admin")!.Value.SchemeId);
        Assert.Equal("MerchantUserSession", ApiHost::Api.Iam.AuthPolicyScheme.For("merchant-user")!.Value.SchemeId);
    }
}
