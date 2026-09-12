extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Domain;
using Platform.Application.Transactions;
using SharedKernel;

namespace Hosts.Tests;

[Trait("Capability", "CheckoutTransactions")]
public sealed class Task6CheckoutConfirmHostTests
{
    private static readonly Guid MerchantId = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrderId = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");
    private static readonly Guid LinkId = Guid.Parse("c0000000-0000-0000-0000-0000000000c1");
    private static readonly DateTime ExpiresAt = DateTime.UtcNow.AddHours(1);

    [Fact]
    [Trait("Requirement", "REQ-7.6")]
    [Trait("Requirement", "REQ-7.7")]
    public async Task Confirm_rejects_cookie_or_order_mismatch_before_transaction_or_provider()
    {
        var reader = new FakeCheckoutReader
        {
            Summary = Summary(),
        };
        var capability = new FakeCapabilityService
        {
            Current = new CheckoutCapability(OrderId, LinkId, 7, "proof", "csrf", ExpiresAt, "tab-a"),
        };
        using var factory = new ConfirmFactory(reader, capability);
        using var client = factory.CreateClient();

        using var cookieMismatch = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/confirm");
        cookieMismatch.Headers.Add("Cookie", "checkout_capability=other-proof");
        cookieMismatch.Headers.Add("Idempotency-Key", "confirm-1");
        cookieMismatch.Content = JsonContent.Create(Body("proof", OrderId, 7));
        using var first = await client.SendAsync(cookieMismatch);
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);

        using var orderMismatch = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/confirm");
        orderMismatch.Headers.Add("Cookie", "checkout_capability=proof");
        orderMismatch.Headers.Add("Idempotency-Key", "confirm-2");
        orderMismatch.Content = JsonContent.Create(Body("proof", Guid.NewGuid(), 7));
        using var second = await client.SendAsync(orderMismatch);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        Assert.Equal(0, factory.TransactionStore.Calls);
    }

    [Fact]
    [Trait("Requirement", "REQ-7.6")]
    [Trait("Requirement", "REQ-7.7")]
    public async Task Confirm_rejects_stale_order_version_and_wrong_csrf_before_write()
    {
        var reader = new FakeCheckoutReader { Summary = Summary() };
        var capability = new FakeCapabilityService
        {
            Current = new CheckoutCapability(OrderId, LinkId, 7, "proof", "csrf", ExpiresAt, "tab-a"),
        };
        using var factory = new ConfirmFactory(reader, capability);
        using var client = factory.CreateClient();

        using var stale = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/confirm");
        stale.Headers.Add("Cookie", "checkout_capability=proof");
        stale.Headers.Add("Idempotency-Key", "confirm-3");
        stale.Content = JsonContent.Create(Body("proof", OrderId, 6));
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);

        using var csrf = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/confirm");
        csrf.Headers.Add("Cookie", "checkout_capability=proof");
        csrf.Headers.Add("Idempotency-Key", "confirm-4");
        csrf.Content = JsonContent.Create(Body("proof", OrderId, 7, csrfToken: "wrong"));
        using var csrfResponse = await client.SendAsync(csrf);
        Assert.Equal(HttpStatusCode.Forbidden, csrfResponse.StatusCode);

        Assert.Equal(0, factory.TransactionStore.Calls);
    }

    private static CheckoutOrderSnapshot Summary() => new(
        OrderId,
        LinkId,
        7,
        PaymentLinkStatus.Active,
        ExpiresAt,
        "ORD6900000001",
        "Merchant",
        OrderStatus.Open,
        PaymentStatus.Unpaid,
        Money.Of(100m, "THB"),
        [new CheckoutSummaryLine("DOC-1", "VMI", "Document", 1, Money.Of(100m, "THB"))],
        MerchantId);

    private static object Body(string proof, Guid orderId, long version, string csrfToken = "csrf") => new
    {
        orderId,
        orderVersion = version,
        proof,
        csrfToken,
        paymentMethod = "card",
    };

    private sealed class FakeCheckoutReader : ICustomerCheckoutReader
    {
        public CheckoutOrderSnapshot? Summary { get; init; }
        public Task<CheckoutLinkSnapshot?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult<CheckoutLinkSnapshot?>(null);
        public Task<CheckoutOrderSnapshot?> GetSummaryAsync(Guid orderId, Guid linkId, CancellationToken cancellationToken) =>
            Task.FromResult(Summary?.OrderId == orderId && Summary.LinkId == linkId ? Summary : null);
    }

    private sealed class FakeCapabilityService : ICheckoutCapabilityService
    {
        public CheckoutCapability? Current { get; init; }
        public CheckoutCapability Issue(Guid orderId, Guid linkId, long orderVersion, DateTime linkExpiresAt, DateTime now) =>
            Current ?? throw new InvalidOperationException();
        public bool TryRead(string proof, out CheckoutCapability capability)
        {
            capability = Current!;
            return Current is not null && proof == Current.Proof;
        }
    }

    private sealed class ConfirmFactory : WebApplicationFactory<ApiHost::Program>
    {
        public readonly RecordingTransactionStore TransactionStore = new();
        public ConfirmFactory(FakeCheckoutReader reader, FakeCapabilityService capability)
        {
            Reader = reader;
            Capability = capability;
        }

        private FakeCheckoutReader Reader { get; }
        private FakeCapabilityService Capability { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Migrator", "");
            builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<ICustomerCheckoutReader>(Reader);
                services.AddSingleton<ICheckoutCapabilityService>(Capability);
                services.AddSingleton<ICheckoutTransactionStore>(TransactionStore);
            });
        }
    }

    private sealed class RecordingTransactionStore : ICheckoutTransactionStore
    {
        public int Calls { get; private set; }
        public Task<CheckoutTransactionContext?> GetCheckoutForUpdateAsync(
            Guid merchantId, Guid orderId, Guid linkId, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("guard should reject before transaction store");
        }
    }
}
