extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Checkouts.Application;
using Checkouts.Domain;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Domain;
using Persistence.MerchantRuntime.Orders;
using SharedKernel;

namespace Hosts.Tests;

[Trait("Capability", "OrdersLinks")]
public sealed class Task5CheckoutEndpointsTests
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid LinkId = Guid.NewGuid();
    private static readonly DateTime ExpiresAt = DateTime.UtcNow.AddHours(1);

    [Fact]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.4")]
    [Trait("Requirement", "REQ-7.5")]
    public async Task Access_accepts_token_only_in_body_sets_http_only_secure_cookie_and_summary_is_redacted()
    {
        var reader = new FakeCheckoutReader
        {
            Link = new CheckoutLinkSnapshot(
                LinkId, Guid.NewGuid(), OrderId, PaymentLinkStatus.Active, DateTime.UtcNow,
                ExpiresAt, null, 1, OrderStatus.Open, PaymentStatus.Unpaid, 7),
            Summary = new CheckoutOrderSnapshot(
                OrderId, LinkId, 7, PaymentLinkStatus.Active, ExpiresAt, "ORD6900000001",
                "Merchant name", OrderStatus.Open, PaymentStatus.Unpaid, Money.Of(194m, "THB"),
                [new CheckoutSummaryLine("DOC-1", "VMI", "Product", 1, Money.Of(194m, "THB"))]),
        };
        using var factory = new CheckoutFactory(
            reader,
            new DataProtectedCheckoutCapabilityService(new EphemeralDataProtectionProvider()));
        using var client = factory.CreateClient();

        using var access = await client.PostAsJsonAsync(
            "/api/v1/checkout/access", new { token = "raw-token-from-body" });

        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        Assert.Contains(access.Headers.GetValues("Set-Cookie"), cookie =>
            cookie.StartsWith("checkout_capability=", StringComparison.Ordinal)
            && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase)
            && cookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        var accessJson = await access.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(OrderId, accessJson.GetProperty("orderId").GetGuid());
        Assert.Equal(7, accessJson.GetProperty("orderVersion").GetInt64());
        Assert.False(accessJson.GetProperty("proof").GetString()!.StartsWith("eyJ", StringComparison.Ordinal));

        using var summaryRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/checkout/summary");
        summaryRequest.Headers.Add(
            "Cookie", $"checkout_capability={accessJson.GetProperty("proof").GetString()}");
        using var summary = await client.SendAsync(summaryRequest);

        Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
        var json = await summary.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ORD6900000001", json.GetProperty("orderNo").GetString());
        Assert.Equal("Merchant name", json.GetProperty("merchantName").GetString());
        Assert.Equal("194.0000", json.GetProperty("totalAmount").GetProperty("amount").GetString());
        Assert.Single(json.GetProperty("lines").EnumerateArray());
        foreach (var forbidden in new[]
        {
            "rawToken", "protectedRawToken", "tokenHash", "orderSnapshot", "merchantId",
            "provider", "paymentSessionId", "sessionId", "ownerSaleId", "ownerBranchIdAtCreation",
            "metadata", "customerEmail", "customerPhone"
        })
            Assert.False(json.TryGetProperty(forbidden, out _), $"summary leaked {forbidden}");
    }

    [Fact]
    [Trait("Requirement", "REQ-7.4")]
    [Trait("Requirement", "REQ-7.5")]
    public async Task Access_rejects_unknown_revoked_and_expired_tokens_and_summary_rejects_cookie_mismatch()
    {
        var reader = new FakeCheckoutReader();
        var capability = new FakeCapabilityService();
        using var factory = new CheckoutFactory(reader, capability);
        using var client = factory.CreateClient();

        using var unknown = await client.PostAsJsonAsync(
            "/api/v1/checkout/access", new { token = "unknown" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        reader.Link = new CheckoutLinkSnapshot(
            LinkId, Guid.NewGuid(), OrderId, PaymentLinkStatus.Revoked, DateTime.UtcNow,
            ExpiresAt, DateTime.UtcNow, 2, OrderStatus.Open, PaymentStatus.Unpaid, 7);
        using var revoked = await client.PostAsJsonAsync(
            "/api/v1/checkout/access", new { token = "revoked" });
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);

        reader.Link = reader.Link with
        {
            LinkStatus = PaymentLinkStatus.Active,
            LinkExpiresAt = DateTime.UtcNow.AddMinutes(-1),
            LinkRevokedAt = null,
        };
        using var expired = await client.PostAsJsonAsync(
            "/api/v1/checkout/access", new { token = "expired" });
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);

        reader.Summary = new CheckoutOrderSnapshot(
            OrderId, LinkId, 7, PaymentLinkStatus.Active, ExpiresAt, "ORD6900000001", "Merchant",
            OrderStatus.Open, PaymentStatus.Unpaid, Money.Of(1m, "THB"), []);
        capability.Current = new CheckoutCapability(OrderId, LinkId, 7, "proof", "csrf", ExpiresAt);
        using var mismatch = new HttpRequestMessage(HttpMethod.Get, "/api/v1/checkout/summary");
        mismatch.Headers.Add("Cookie", "checkout_capability=proof");
        mismatch.Headers.Add("X-Checkout-Proof", "other-proof");
        using var mismatchResponse = await client.SendAsync(mismatch);
        Assert.Equal(HttpStatusCode.Forbidden, mismatchResponse.StatusCode);
    }

    private sealed class FakeCheckoutReader : ICustomerCheckoutReader
    {
        public CheckoutLinkSnapshot? Link { get; set; }
        public CheckoutOrderSnapshot? Summary { get; set; }
        public Task<CheckoutLinkSnapshot?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult(Link);
        public Task<CheckoutOrderSnapshot?> GetSummaryAsync(Guid orderId, Guid linkId, CancellationToken cancellationToken) =>
            Task.FromResult(Summary);
    }

    private sealed class FakeCapabilityService : ICheckoutCapabilityService
    {
        public CheckoutCapability? Current { get; set; }

        public CheckoutCapability Issue(Guid orderId, Guid linkId, long orderVersion, DateTime linkExpiresAt, DateTime now) =>
            Current ??= new CheckoutCapability(orderId, linkId, orderVersion, "proof", "csrf", linkExpiresAt);

        public bool TryRead(string proof, out CheckoutCapability capability)
        {
            capability = Current!;
            return Current is not null && proof == Current.Proof;
        }
    }

    private sealed class CheckoutFactory(
        FakeCheckoutReader reader,
        ICheckoutCapabilityService capability) : WebApplicationFactory<ApiHost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Migrator", "");
            builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
            builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IPaymentLinkTokenService>(new PaymentLinkTokenService(new byte[32]));
                services.AddScoped<ICustomerCheckoutReader>(_ => reader);
                services.AddScoped<ICheckoutCapabilityService>(_ => capability);
            });
        }
    }
}
