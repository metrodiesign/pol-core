extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
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
        new("POST", "/api/v1/carts", "dual-console", "payment.create"),
        new("GET", "/api/v1/carts/{cartId:guid}", "dual-console", "payment.view"),
        new("POST", "/api/v1/carts/{cartId:guid}/items", "dual-console", "payment.create"),
        new("PUT", "/api/v1/carts/{cartId:guid}/items/{itemId:guid}", "dual-console", "payment.create"),
        new("DELETE", "/api/v1/carts/{cartId:guid}/items/{itemId:guid}", "dual-console", "payment.create"),
        new("POST", "/api/v1/carts/{cartId:guid}/clear", "dual-console", "payment.create"),
        new("POST", "/api/v1/payments/sessions", "dual-console", "payment.create"),
        new("GET", "/api/v1/payments/sessions", "merchant-user", "payment.view"),
        new("GET", "/api/v1/payments/sessions/{paymentSessionId:guid}", "dual-console", "payment.view"),
        new("POST", "/api/v1/payments/sessions/{paymentSessionId:guid}/redirect", "dual-console", "payment.redirect"),
        new("GET", "/api/v1/payments/methods", "merchant-user", "payment.view"),
        new("GET", "/api/v1/payments/methods/{method}/options", "merchant-user", "payment.view"),
        new("POST", "/api/v1/orders", "identity-platform", "payment.create"),
        new("POST", "/api/v1/orders/from-cart", "dual-console", "payment.create"),
        new("GET", "/api/v1/orders", "dual-console", "payment.view"),
        new("GET", "/api/v1/orders/{orderId:guid}", "dual-console", "payment.view"),
        new("POST", "/api/v1/orders/{orderId:guid}/cancel", "dual-console", "payment.create"),
        new("POST", "/api/v1/orders/{orderId:guid}/summary/resend", "dual-console", "payment.create"),
        new("GET", "/api/v1/reports/reconciliation", "dual-console", "payment.view"),
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

        // --- Task10 canonical Control Plane and commerce additions ---
        new("GET", "/api/v1/accounts", "admin", "user.manage"),
        new("GET", "/api/v1/accounts/{accountId:guid}", "admin", "user.manage"),
        new("PATCH", "/api/v1/accounts/{accountId:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/accounts/{accountId:guid}/merchant-access", "admin", "user.manage"),
        new("DELETE", "/api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}", "admin", "user.manage"),
        new("PUT", "/api/v1/accounts/{accountId:guid}/merchant-access/{merchantId:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/accounts/{accountId:guid}/platform-access", "admin", "user.manage"),
        new("PUT", "/api/v1/accounts/{accountId:guid}/platform-access", "admin", "user.manage"),
        new("POST", "/api/v1/accounts/{accountId:guid}/session-revocations", "admin", "user.manage"),
        new("GET", "/api/v1/agent-registrations/", "admin", "merchants.users.view"),
        new("GET", "/api/v1/agent-registrations/{registrationId:guid}", "admin", "merchants.users.view"),
        new("GET", "/api/v1/agent-registrations/{registrationId:guid}/attempts", "admin", "merchants.users.view"),
        new("POST", "/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/approve", "admin", "merchants.users.approve"),
        new("POST", "/api/v1/agent-registrations/{registrationId:guid}/attempts/{attemptId:guid}/reject", "admin", "merchants.users.reject"),
        new("GET", "/api/v1/audit-logs", "admin", "audit.view"),
        new("GET", "/api/v1/checkout/payment-methods", "merchant-user", "payment.view"),
        new("GET", "/api/v1/merchants/{merchantId:guid}", "admin", "merchant.view"),
        new("PATCH", "/api/v1/merchants/{merchantId:guid}", "admin", "merchant.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/branches", "admin", "merchant.view"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/branches", "admin", "merchant.manage"),
        new("PATCH", "/api/v1/merchants/{merchantId:guid}/branches/{branchId:guid}", "admin", "merchant.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/event-endpoint", "admin", "settings.manage"),
        new("PUT", "/api/v1/merchants/{merchantId:guid}/event-endpoint", "admin", "settings.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/payment-setting-requests", "admin", "settings.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/payment-setting-requests", "admin", "settings.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/approve", "admin", "settings.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/payment-setting-requests/{requestId:guid}/reject", "admin", "settings.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/payment-settings", "admin", "settings.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/provider-accounts", "admin", "settings.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/provider-accounts", "admin", "settings.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}", "admin", "settings.manage"),
        new("PATCH", "/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/connection-tests", "admin", "settings.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions", "admin", "settings.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/credential-versions", "admin", "settings.manage"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/provider-accounts/{providerAccountId:guid}/disable", "admin", "settings.manage"),
        new("GET", "/api/v1/merchants/{merchantId:guid}/sales", "admin", "merchant.view"),
        new("POST", "/api/v1/merchants/{merchantId:guid}/sales", "admin", "merchant.manage"),
        new("PATCH", "/api/v1/merchants/{merchantId:guid}/sales/{saleId:guid}", "admin", "merchant.manage"),
        new("GET", "/api/v1/notification-deliveries/{deliveryId:guid}", "admin", "settings.manage"),
        new("GET", "/api/v1/notification-deliveries/{deliveryId:guid}/attempts", "admin", "settings.manage"),
        new("POST", "/api/v1/notification-deliveries/{deliveryId:guid}/retries", "admin", "settings.manage"),
        new("GET", "/api/v1/notifications", "admin", "settings.manage"),
        new("GET", "/api/v1/notifications/{notificationId:guid}", "admin", "settings.manage"),
        new("PATCH", "/api/v1/orders/{orderId:guid}", "admin-or-identity-order", "payment.create"),
        new("GET", "/api/v1/orders/{orderId:guid}/history", "admin-or-identity-order", "payment.view"),
        new("POST", "/api/v1/orders/{orderId:guid}/issue", "dual-console", "payment.create"),
        new("GET", "/api/v1/orders/{orderId:guid}/items", "admin-or-identity-order", "payment.view"),
        new("GET", "/api/v1/orders/{orderId:guid}/payment-links", "dual-console", "payment.view"),
        new("POST", "/api/v1/orders/{orderId:guid}/payment-links", "dual-console", "payment.create"),
        new("POST", "/api/v1/payment-links/{linkId:guid}/revoke", "dual-console", "payment.create"),
        new("GET", "/api/v1/payment-providers", "admin", "settings.manage"),
        new("GET", "/api/v1/payment-providers/{providerId:guid}/methods", "admin", "settings.manage"),
        new("GET", "/api/v1/permissions", "admin", "user.roles"),
        new("GET", "/api/v1/roles", "admin", "user.roles"),
        new("POST", "/api/v1/roles", "admin", "user.roles"),
        new("GET", "/api/v1/roles/{roleId:guid}", "admin", "user.roles"),
        new("PUT", "/api/v1/roles/{roleId:guid}", "admin", "user.roles"),
        new("GET", "/api/v1/system-clients/", "admin", "user.manage"),
        new("POST", "/api/v1/system-clients/", "admin", "user.manage"),
        new("GET", "/api/v1/system-clients/{clientId:guid}", "admin", "user.manage"),
        new("PATCH", "/api/v1/system-clients/{clientId:guid}", "admin", "user.manage"),
        new("PUT", "/api/v1/system-clients/{clientId:guid}/access", "admin", "settings.manage"),
        new("GET", "/api/v1/system-clients/{clientId:guid}/keys", "admin", "user.manage"),
        new("POST", "/api/v1/system-clients/{clientId:guid}/keys", "admin", "user.manage"),
        new("DELETE", "/api/v1/system-clients/{clientId:guid}/keys/{keyId:guid}", "admin", "user.manage"),
        new("GET", "/api/v1/transactions", "admin", "payment.view"),
        new("GET", "/api/v1/transactions/{transactionId:guid}", "admin", "payment.view"),
        new("GET", "/api/v1/transactions/{transactionId:guid}/events", "admin", "payment.view"),
        new("POST", "/api/v1/transactions/{transactionId:guid}/review-notes", "admin", "payment.view"),
        new("POST", "/api/v1/transactions/{transactionId:guid}/verify", "admin", "payment.view"),
        // --- admin ---
        new("GET", "/api/v1/products/documents", "admin", "txn.view"),
        new("GET", "/api/v1/orders/export", "admin", "txn.export"),
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
        new("GET", "/api/v1/payments/methods/{method}", "admin", "merchant.view"),
        new("PUT", "/api/v1/payments/methods/{method}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/providers/{providerCode}", "admin", "merchant.view"),
        new("PUT", "/api/v1/payments/providers/{providerCode}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/providers/{providerCode}/methods/{method}", "admin", "merchant.view"),
        new("PUT", "/api/v1/payments/providers/{providerCode}/methods/{method}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}", "admin", "merchant.view"),
        new("PUT", "/api/v1/payments/providers/{providerCode}/methods/{method}/options/{option}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/merchants/{merchantId:guid}/methods", "admin", "merchant.view"),
        new("GET", "/api/v1/payments/merchants/{merchantId:guid}/methods/{method}", "admin", "merchant.view"),
        new("PUT", "/api/v1/payments/merchants/{merchantId:guid}/methods/{method}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods", "admin", "merchants.users.view"),
        new("GET", "/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}", "admin", "merchants.users.view"),
        new("PUT", "/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}", "admin", "merchants.users.manage"),
        new("GET", "/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/options", "admin", "merchants.users.view"),
        new("GET", "/api/v1/payments/merchants/{merchantId:guid}/users/{userId:guid}/methods/{method}/resolution", "admin", "merchants.users.view"),
        new("GET", "/api/v1/originators", "admin", "merchant.view"),
        new("GET", "/api/v1/originators/{originatorId:guid}", "admin", "merchant.view"),
        new("POST", "/api/v1/originators", "admin", "merchant.manage"),
        new("PUT", "/api/v1/originators/{originatorId:guid}", "admin", "merchant.manage"),
        new("POST", "/api/v1/originators/{originatorId:guid}/enable", "admin", "merchant.manage"),
        new("POST", "/api/v1/originators/{originatorId:guid}/disable", "admin", "merchant.manage"),
        new("DELETE", "/api/v1/originators/{originatorId:guid}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/merchant-settings/{merchantId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/merchant-settings/{merchantId:guid}/environment-change-requests", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/psp-connections", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/psp-connections/{connectionId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/psp-connections", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/psp-connections", "admin", "merchant.manage"),
        new("PUT", "/api/v1/payments/psp-connections/{connectionId:guid}", "admin", "settings.manage"),
        new("PUT", "/api/v1/payments/psp-connections/{connectionId:guid}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}", "admin", "merchant.view"),
        new("PUT", "/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}", "admin", "merchant.manage"),
        new("GET", "/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}", "admin", "merchant.view"),
        new("PUT", "/api/v1/payments/psp-connections/{connectionId:guid}/methods/{method}/options/{option}", "admin", "merchant.manage"),
        new("POST", "/api/v1/payments/psp-connections/{connectionId:guid}/test", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/psp-connections/{connectionId:guid}/credential-change-requests", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/psp-connections/{connectionId:guid}/credential-change-requests/{approvalId:guid}/test", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/routing-rulesets", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/routing-rulesets/{rulesetId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/routing-rulesets", "admin", "settings.manage"),
        new("PUT", "/api/v1/payments/routing-rulesets/{rulesetId:guid}", "admin", "settings.manage"),
        new("DELETE", "/api/v1/payments/routing-rulesets/{rulesetId:guid}", "admin", "settings.manage"),
        new("POST", "/api/v1/payments/routing-rulesets/{rulesetId:guid}/activation-requests", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/merchant-settings/{merchantId:guid}/simple-routing", "admin", "settings.manage"),
        new("PUT", "/api/v1/payments/merchant-settings/{merchantId:guid}/simple-routing", "admin", "settings.manage"),
        new("GET", "/api/v1/payments/transactions", "admin", "txn.view"),
        new("GET", "/api/v1/payments/transactions/{paymentSessionId:guid}", "admin", "txn.view"),
        new("GET", "/api/v1/payments/transactions/export", "admin", "txn.export"),
        new("GET", "/api/v1/reports/dashboard", "admin", "txn.view"),
        new("GET", "/api/v1/reports/operations", "admin", "txn.view"),
        new("GET", "/api/v1/reports/operations/export", "admin", "txn.export"),
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
    public void Exactly_220_active_gate_sites_are_pinned() => Assert.Equal(220, Sites.Length);

    [Fact]
    public void Api078_order_list_carries_the_identity_owner_scope_marker()
    {
        using var factory = new GateFactory();
        using var _ = factory.CreateClient();
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/api/v1/orders"
                && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Get) == true);

        Assert.NotNull(endpoint.Metadata.GetMetadata<ApiHost::Api.Iam.IdentityPermissionAuthorization.IdentityOrderPermissionMarker>());
        Assert.Contains(endpoint.Metadata.OfType<ApiHost::Api.Iam.RequiredPermission>(),
            required => required.Permission == "payment.view");
    }

    // REQ-10.3: the scheme ids themselves — a rename here would be a breaking contract change for both SPAs.
    [Fact]
    public void Auth_policy_scheme_mapping_pins_the_literal_scheme_ids()
    {
        Assert.Equal("AdminSession", ApiHost::Api.Iam.AuthPolicyScheme.For("admin")!.Value.SchemeId);
        Assert.Equal("MerchantUserSession", ApiHost::Api.Iam.AuthPolicyScheme.For("merchant-user")!.Value.SchemeId);
        Assert.Equal(
            ["AdminSession", "MerchantUserSession"],
            ApiHost::Api.Iam.AuthPolicyScheme.AllFor("dual-console").Select(x => x.SchemeId));
        Assert.Equal(
            ["AdminSession", "IdentityPlatform"],
            ApiHost::Api.Iam.AuthPolicyScheme.AllFor(
                ApiHost::Api.Iam.ConsoleSessionAuthentication.AdminOrIdentityOrderPolicyName)
                .Select(x => x.SchemeId));
    }
}
