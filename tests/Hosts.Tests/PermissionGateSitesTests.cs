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
// Pins every physical route carrying RequiredPermission or RequiredAudiencePermission metadata. A dual-console
// route contributes one logical site per audience, so either side changing cannot leave this table stale.

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
        new("GET", "/api/v1/merchants/users/", "merchant-user", "users.view"),
        new("GET", "/api/v1/merchants/users/{merchantUserId:guid}", "merchant-user", "users.view"),
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
        new("GET", "/api/v1/products/documents", "admin", "txn.view"),
        new("POST", "/api/v1/carts", "admin", "txn.manage"),
        new("GET", "/api/v1/carts/{cartId:guid}", "admin", "txn.view"),
        new("POST", "/api/v1/carts/{cartId:guid}/items", "admin", "txn.manage"),
        new("PUT", "/api/v1/carts/{cartId:guid}/items/{itemId:guid}", "admin", "txn.manage"),
        new("DELETE", "/api/v1/carts/{cartId:guid}/items/{itemId:guid}", "admin", "txn.manage"),
        new("POST", "/api/v1/carts/{cartId:guid}/clear", "admin", "txn.manage"),
        new("POST", "/api/v1/payments/sessions", "admin", "txn.manage"),
        new("GET", "/api/v1/payments/sessions/{paymentSessionId:guid}", "admin", "txn.view"),
        new("POST", "/api/v1/payments/sessions/{paymentSessionId:guid}/redirect", "admin", "txn.manage"),
        new("POST", "/api/v1/orders", "admin", "txn.manage"),
        new("GET", "/api/v1/orders", "admin", "txn.view"),
        new("GET", "/api/v1/orders/export", "admin", "txn.export"),
        new("GET", "/api/v1/orders/{orderId:guid}", "admin", "txn.view"),
        new("POST", "/api/v1/orders/{orderId:guid}/cancel", "admin", "txn.manage"),
        new("POST", "/api/v1/orders/{orderId:guid}/summary/resend", "admin", "txn.manage"),
        new("GET", "/api/v1/merchants/users/", "admin", "merchants.users.view"),
        new("GET", "/api/v1/merchants/users/{merchantUserId:guid}", "admin", "merchants.users.view"),
        new("POST", "/api/v1/admins/merchants/users/{merchantUserId:guid}/approve", "admin", "merchants.users.approve"),
        new("POST", "/api/v1/admins/merchants/users/{merchantUserId:guid}/reject", "admin", "merchants.users.reject"),
        new("GET", "/api/v1/admins/merchants/users/{merchantUserId:guid}/registrations", "admin", "merchants.users.view"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/edit", "admin", "merchants.users.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/user-invitations", "admin", "merchants.users.manage"),
        new("PUT", "/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}", "admin", "merchants.users.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/roles", "admin", "merchants.roles.view"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/roles/{code}", "admin", "merchants.roles.view"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/permissions", "admin", "merchants.roles.view"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/roles", "admin", "merchants.roles.manage"),
        new("PUT", "/api/v1/merchants/{merchantId:guid}/roles/{code}", "admin", "merchants.roles.manage"),
        new("DELETE", "/api/v1/merchants/{merchantId:guid}/roles/{code}", "admin", "merchants.roles.manage"),
        new("PUT", "/api/v1/merchants/{merchantId:guid}/users/{merchantUserId:guid}/roles", "admin", "merchants.roles.manage"),
        new("GET", "/api/v1/admins", "admin", "user.view"),
        new("GET", "/api/v1/admins/{id:guid}", "admin", "user.view"),
        new("GET", "/api/v1/admins/{id:guid}/effective-permissions", "admin", "user.view"),
        new("PUT", "/api/v1/admins/{id:guid}/profile", "admin", "user.manage"),
        new("GET", "/api/v1/positions", "admin", "user.view"),
        new("GET", "/api/v1/positions/{id:guid}", "admin", "user.view"),
        new("POST", "/api/v1/positions", "admin", "user.manage"),
        new("PUT", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/positions/{id:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/offices", "admin", "user.view"),
        new("GET", "/api/v1/offices/{id:guid}", "admin", "user.view"),
        new("POST", "/api/v1/offices", "admin", "user.manage"),
        new("PUT", "/api/v1/offices/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/offices/{id:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/levels", "admin", "user.view"),
        new("GET", "/api/v1/levels/{id:guid}", "admin", "user.view"),
        new("POST", "/api/v1/levels", "admin", "user.manage"),
        new("PUT", "/api/v1/levels/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/levels/{id:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/divisions", "admin", "user.view"),
        new("GET", "/api/v1/divisions/{id:guid}", "admin", "user.view"),
        new("POST", "/api/v1/divisions", "admin", "user.manage"),
        new("PUT", "/api/v1/divisions/{id:guid}", "admin", "user.manage"),
        new("DELETE", "/api/v1/divisions/{id:guid}", "admin", "user.manage"),
        new("POST", "/api/v1/admins/roles", "admin", "user.roles"),
        new("PUT", "/api/v1/admins/roles/{code}", "admin", "user.roles"),
        new("DELETE", "/api/v1/admins/roles/{code}", "admin", "user.roles"),
        new("PUT", "/api/v1/admins/{id:guid}/roles", "admin", "user.roles"),
        new("GET", "/api/v1/approvals", "admin", "settings.manage"),
        new("GET", "/api/v1/approvals/{approvalId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/approvals/{approvalId:guid}/approve", "admin", "settings.manage"),
        new("POST", "/api/v1/approvals/{approvalId:guid}/reject", "admin", "settings.manage"),
        new("GET", "/api/v1/audits", "admin", "audit.view"),
        new("GET", "/api/v1/audits/{auditId:guid}", "admin", "audit.view"),
        new("GET", "/api/v1/merchants", "admin", "merchant.view"),
        new("GET", "/api/v1/merchants/{code}", "admin", "merchant.view"),
        new("PUT", "/api/v1/merchants/{merchantId:guid}", "admin", "merchant.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/suspend", "admin", "merchant.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/reactivate", "admin", "merchant.manage"),
        new("GET", "/api/v1/originators", "admin", "merchant.view"),
        new("GET", "/api/v1/originators/{originatorId:guid}", "admin", "merchant.view"),
        new("POST", "/api/v1/originators", "admin", "merchant.manage"),
        new("PUT", "/api/v1/originators/{originatorId:guid}", "admin", "merchant.manage"),
        new("POST", "/api/v1/originators/{originatorId:guid}/enable", "admin", "merchant.manage"),
        new("POST", "/api/v1/originators/{originatorId:guid}/disable", "admin", "merchant.manage"),
        new("DELETE", "/api/v1/originators/{originatorId:guid}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/psp-connections", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/psp-connections/{connectionId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/psp-connections", "admin", "settings.manage"),
        new("PUT", "/api/v1/payments/psp-connections/{connectionId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/psp-connections/{connectionId:guid}/test", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/psp-connections/{connectionId:guid}/credential-change-requests", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/routing-rulesets", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/routing-rulesets/{rulesetId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/routing-rulesets", "admin", "settings.manage"),
        new("PUT", "/api/v1/payments/routing-rulesets/{rulesetId:guid}", "admin", "settings.manage"),
        new("DELETE", "/api/v1/payments/routing-rulesets/{rulesetId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/routing-rulesets/{rulesetId:guid}/activation-requests", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/transactions", "admin", "txn.view"),
        new("GET", "/api/v1/payments/transactions/{paymentSessionId:guid}", "admin", "txn.view"),
        new("GET", "/api/v1/payments/transactions/export", "admin", "txn.export"),
        new("GET", "/api/v1/reports/dashboard", "admin", "txn.view"),
        new("GET", "/api/v1/reports/operations", "admin", "txn.view"),
        new("GET", "/api/v1/reports/operations/export", "admin", "txn.export"),
        new("GET", "/api/v1/reports/reconciliation", "admin", "txn.view"),
        new("GET", "/api/v1/api-clients", "admin", "apikey.manage"),
        new("POST", "/api/v1/api-clients", "admin", "apikey.manage"),
        new("GET", "/api/v1/api-clients/{clientId:guid}", "admin", "apikey.manage"),
        new("PUT", "/api/v1/api-clients/{clientId:guid}", "admin", "apikey.manage"),
        new("POST", "/api/v1/api-clients/{clientId:guid}/revoke", "admin", "apikey.manage"),
        new("POST", "/api/v1/api-clients/{clientId:guid}/secret-rotation-requests", "admin", "apikey.manage"),
        new("POST", "/api/v1/api-clients/secrets/{ticketId}/reveal", "admin", "apikey.manage"),
        new("GET", "/api/v1/webhooks/endpoints", "admin", "settings.manage"),
        new("POST", "/api/v1/webhooks/endpoints", "admin", "settings.manage"),
        new("GET", "/api/v1/webhooks/endpoints/{endpointId:guid}", "admin", "settings.manage"),
        new("PUT", "/api/v1/webhooks/endpoints/{endpointId:guid}", "admin", "settings.manage"),
        new("DELETE", "/api/v1/webhooks/endpoints/{endpointId:guid}", "admin", "settings.manage"),
        new("GET", "/api/v1/webhooks/deliveries", "admin", "settings.manage"),
        new("GET", "/api/v1/webhooks/deliveries/{deliveryId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/webhooks/deliveries/{deliveryId:guid}/replay", "admin", "settings.manage"),
        new("GET", "/api/v1/webhooks/inbound-events", "admin", "audit.view"),
        new("GET", "/api/v1/webhooks/inbound-events/{eventId:guid}", "admin", "audit.view"),
        new("GET", "/api/v1/notifications/rules", "admin", "settings.manage"),
        new("POST", "/api/v1/notifications/rules", "admin", "settings.manage"),
        new("GET", "/api/v1/notifications/rules/{ruleId:guid}", "admin", "settings.manage"),
        new("PUT", "/api/v1/notifications/rules/{ruleId:guid}", "admin", "settings.manage"),
        new("DELETE", "/api/v1/notifications/rules/{ruleId:guid}", "admin", "settings.manage"),
        new("GET", "/api/v1/notifications/deliveries", "admin", "settings.manage"),
        new("GET", "/api/v1/notifications/deliveries/{deliveryId:guid}", "admin", "settings.manage"),
    ];

    [Fact]
    public void Every_active_gate_site_is_pinned_with_expected_policy_and_key()
    {
        using var factory = new GateFactory();
        using var _ = factory.CreateClient();

        var expected = Sites.Select(s => (s.Method, s.Route, s.Policy, s.Key)).ToHashSet();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();
        var direct = endpoints
            .SelectMany(endpoint => endpoint.Metadata.OfType<ApiHost::Api.Iam.RequiredPermission>()
                .SelectMany(required => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    ?? Array.Empty<string>())
                    .Select(method => (
                        Method: method,
                        Route: endpoint.RoutePattern.RawText!,
                        Policy: endpoint.Metadata.OfType<IAuthorizeData>()
                            .Select(a => a.Policy).Last(p => !string.IsNullOrEmpty(p))!,
                        Key: required.Permission))))
            .ToArray();
        var audience = endpoints
            .SelectMany(endpoint => endpoint.Metadata.OfType<ApiHost::Api.Iam.RequiredAudiencePermission>()
                .SelectMany(required => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                    ?? Array.Empty<string>())
                    .SelectMany(method => new[]
                    {
                        (Method: method, Route: endpoint.RoutePattern.RawText!,
                            Policy: "admin", Key: required.AdminKey),
                        (Method: method, Route: endpoint.RoutePattern.RawText!,
                            Policy: "merchant-user", Key: required.MerchantKey),
                    })))
            .ToArray();
        var actual = direct.Concat(audience)
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
    public void Exactly_154_active_gate_sites_are_pinned() => Assert.Equal(154, Sites.Length);

    // REQ-10.3: the scheme ids themselves — a rename here would be a breaking contract change for both SPAs.
    [Fact]
    public void Auth_policy_scheme_mapping_pins_the_literal_scheme_ids()
    {
        Assert.Equal("AdminSession", ApiHost::Api.Iam.AuthPolicyScheme.For("admin")!.Value.SchemeId);
        Assert.Equal("MerchantUserSession", ApiHost::Api.Iam.AuthPolicyScheme.For("merchant-user")!.Value.SchemeId);
        Assert.Equal(
            ["AdminSession", "MerchantUserSession"],
            ApiHost::Api.Iam.AuthPolicyScheme.AllFor("dual-console").Select(x => x.SchemeId));
    }
}
