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
// Pins every physical route carrying RequiredPermission metadata. The completeness test compares this inventory
// with EndpointDataSource, so adding, retiring, or changing a gated route cannot leave this table stale.

file sealed class GateFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
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

public sealed class PermissionGateSitesTests
{
    public sealed record Site(string Method, string Route, string Policy, string Key);

    private static readonly Site[] Sites =
    [
        // --- merchant-user ---
        new("GET", "/api/v1/products", "merchant-user", "payment.view"),
        new("POST", "/api/v1/carts", "merchant-user", "payment.create"),
        new("GET", "/api/v1/carts/{cartId:guid}", "merchant-user", "payment.view"),
        new("POST", "/api/v1/carts/{cartId:guid}/items", "merchant-user", "payment.create"),
        new("PUT", "/api/v1/carts/{cartId:guid}/items/{itemId:guid}", "merchant-user", "payment.create"),
        new("DELETE", "/api/v1/carts/{cartId:guid}/items/{itemId:guid}", "merchant-user", "payment.create"),
        new("POST", "/api/v1/carts/{cartId:guid}/clear", "merchant-user", "payment.create"),
        new("POST", "/api/v1/payments/sessions", "merchant-user", "payment.create"),
        new("GET", "/api/v1/payments/sessions", "merchant-user", "payment.view"),
        new("GET", "/api/v1/payments/sessions/{paymentSessionId:guid}", "merchant-user", "payment.view"),
        new("POST", "/api/v1/payments/sessions/{paymentSessionId:guid}/redirect", "merchant-user", "payment.redirect"),
        new("POST", "/api/v1/orders", "merchant-user", "payment.create"),
        new("GET", "/api/v1/orders", "merchant-user", "payment.view"),
        new("GET", "/api/v1/orders/{orderId:guid}", "merchant-user", "payment.view"),
        new("POST", "/api/v1/orders/{orderId:guid}/cancel", "merchant-user", "payment.create"),
        new("POST", "/api/v1/orders/{orderId:guid}/summary/resend", "merchant-user", "payment.create"),
        new("GET", "/api/v1/reports/reconciliation", "merchant-user", "payment.view"),
        new("GET", "/api/v1/merchants/users/", "merchant-user", "users.view"),
        new("GET", "/api/v1/merchants/users/{merchantUserId:guid}", "merchant-user", "users.view"),
        new("GET", "/api/v1/merchants/users/{merchantUserId:guid}/edit", "merchant-user", "users.manage"),
        new("PUT", "/api/v1/merchants/users/{merchantUserId:guid}", "merchant-user", "users.manage"),
        new("POST", "/api/v1/merchants/users/invitations", "merchant-user", "users.manage"),
        new("DELETE", "/api/v1/merchants/users/invitations/{invitationId:guid}", "merchant-user", "users.manage"),
        new("POST", "/api/v1/merchants/users/{merchantUserId:guid}/approve", "merchant-user", "users.manage"),
        new("POST", "/api/v1/merchants/users/{merchantUserId:guid}/reject", "merchant-user", "users.manage"),
        new("POST", "/api/v1/merchants/users/{merchantUserId:guid}/suspend", "merchant-user", "users.manage"),
        new("POST", "/api/v1/merchants/users/{merchantUserId:guid}/reactivate", "merchant-user", "users.manage"),
        new("GET", "/api/v1/merchants/users/permissions", "merchant-user", "roles.view"),
        new("GET", "/api/v1/merchants/users/roles", "merchant-user", "roles.view"),
        new("GET", "/api/v1/merchants/users/roles/{code}", "merchant-user", "roles.view"),
        new("POST", "/api/v1/merchants/users/roles", "merchant-user", "roles.manage"),
        new("PUT", "/api/v1/merchants/users/roles/{code}", "merchant-user", "roles.manage"),
        new("DELETE", "/api/v1/merchants/users/roles/{code}", "merchant-user", "roles.manage"),
        new("PUT", "/api/v1/merchants/users/{merchantUserId:guid}/roles", "merchant-user", "users.roles"),

        // --- admin ---
        new("POST", "/api/v1/admins/merchants/users/{subject}/approve", "admin", "merchants.users.approve"),
        new("POST", "/api/v1/admins/merchants/users/{subject}/reject", "admin", "merchants.users.reject"),
        new("GET", "/api/v1/admins/merchants/users/{subject}/registrations", "admin", "merchants.users.view"),
        new("GET", "/api/v1/admins", "admin", "user.view"),
        new("GET", "/api/v1/admins/{id:guid}", "admin", "user.view"),
        new("GET", "/api/v1/admins/{id:guid}/effective-permissions", "admin", "user.view"),
        new("PUT", "/api/v1/admins/{id:guid}/profile", "admin", "user.manage"),
        new("GET", "/api/v1/positions", "admin", "user.manage"),
        new("GET", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/positions", "admin", "user.manage"),
        new("PUT", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/offices", "admin", "user.manage"),
        new("GET", "/api/v1/offices/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/offices", "admin", "user.manage"),
        new("PUT", "/api/v1/offices/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/offices/{id:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/levels", "admin", "user.manage"),
        new("GET", "/api/v1/levels/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/levels", "admin", "user.manage"),
        new("PUT", "/api/v1/levels/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/levels/{id:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/divisions", "admin", "user.manage"),
        new("GET", "/api/v1/divisions/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/divisions", "admin", "user.manage"),
        new("PUT", "/api/v1/divisions/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/divisions/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/admins/roles", "admin", "user.roles"),
        new("PUT", "/api/v1/admins/roles/{code}", "admin", "user.roles"),
        new("DELETE", "/api/v1/admins/roles/{code}", "admin", "user.roles"),
        new("PUT", "/api/v1/admins/{id:guid}/roles", "admin", "user.roles"),
    ];

    [Fact]
    public void Every_active_gate_site_is_pinned_with_expected_policy_and_key()
    {
        using var factory = new GateFactory();
        using var _ = factory.CreateClient();

        var expected = Sites.Select(s => (s.Method, s.Route, s.Policy, s.Key)).ToHashSet();
        var actual = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => endpoint.Metadata.OfType<ApiHost::Api.Iam.RequiredPermission>()
                .SelectMany(required => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    ?? Array.Empty<string>())
                    .Select(method => (
                        Method: method,
                        Route: endpoint.RoutePattern.RawText!,
                        Policy: endpoint.Metadata.OfType<IAuthorizeData>()
                            .Select(a => a.Policy).Last(p => !string.IsNullOrEmpty(p))!,
                        Key: required.Permission))))
            .ToHashSet();

        var missing = actual.Except(expected)
            .OrderBy(s => s.Route, StringComparer.Ordinal)
            .ThenBy(s => s.Method, StringComparer.Ordinal)
            .Select(s => $"{s.Method} {s.Route} -> {s.Policy}/{s.Key}");
        var retired = expected.Except(actual)
            .OrderBy(s => s.Route, StringComparer.Ordinal)
            .ThenBy(s => s.Method, StringComparer.Ordinal)
            .Select(s => $"{s.Method} {s.Route} -> {s.Policy}/{s.Key}");

        Assert.True(actual.SetEquals(expected),
            $"Missing:\n{string.Join('\n', missing)}\nRetired:\n{string.Join('\n', retired)}");
    }

    [Fact]
    public void Exactly_65_active_gate_sites_are_pinned() => Assert.Equal(65, Sites.Length);

    // REQ-10.3: the scheme ids themselves — a rename here would be a breaking contract change for both SPAs.
    [Fact]
    public void Auth_policy_scheme_mapping_pins_the_literal_scheme_ids()
    {
        Assert.Equal("AdminSession", ApiHost::Api.Iam.AuthPolicyScheme.For("admin")!.Value.SchemeId);
        Assert.Equal("MerchantUserSession", ApiHost::Api.Iam.AuthPolicyScheme.For("merchant-user")!.Value.SchemeId);
    }
}
