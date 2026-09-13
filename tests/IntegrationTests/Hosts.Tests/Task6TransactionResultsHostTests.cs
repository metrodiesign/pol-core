extern alias ApiHost;
using System.Net;
using System.Text.Json;
using BuildingBlocks.Application;
using Checkouts.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orders.Domain;
using Payments.Application.Ports;
using Platform.Application.Transactions;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;

namespace Hosts.Tests;

[Trait("Capability", "CheckoutTransactions")]
public sealed class Task6TransactionResultsHostTests
{
    private static readonly Guid MerchantId = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid OrderId = Guid.Parse("e0000000-0000-0000-0000-0000000000e1");
    private static readonly Guid TransactionId = Guid.Parse("e0000000-0000-0000-0000-0000000000e2");
    private static readonly DateTime ExpiresAt = DateTime.UtcNow.AddMinutes(10);

    [Fact]
    [Trait("Requirement", "REQ-7.10")]
    [Trait("Requirement", "REQ-8.7")]
    public async Task Closed_link_return_mints_status_only_context_without_confirm_capability()
    {
        var order = NewCancelledPaidOrder();
        var transaction = NewSucceededTransaction(order.Id);
        using var factory = new ReturnFactory(order, transaction);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var returned = await client.GetAsync("/api/v1/payment-returns/2c2p?state=return-token");
        Assert.Equal(HttpStatusCode.SeeOther, returned.StatusCode);
        Assert.Equal("/api/v1/checkout/status", returned.Headers.Location?.OriginalString);
        var setCookie = returned.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("checkout_status=return-token", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("checkout_capability=", setCookie, StringComparison.OrdinalIgnoreCase);

        using var status = new HttpRequestMessage(HttpMethod.Get, "/api/v1/checkout/status");
        status.Headers.Add("Cookie", "checkout_status=return-token");
        using var statusResponse = await client.SendAsync(status);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var body = await JsonDocument.ParseAsync(await statusResponse.Content.ReadAsStreamAsync());
        Assert.Equal("paid", body.RootElement.GetProperty("paymentStatus").GetString());
        Assert.Equal(TransactionId, body.RootElement.GetProperty("transactionId").GetGuid());
        Assert.Equal(0, factory.Adapter.CreateCalls);
        Assert.Equal(0, factory.Adapter.FetchCalls);

        using var confirm = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout/confirm");
        confirm.Headers.Add("Cookie", "checkout_status=return-token");
        confirm.Headers.Add("Idempotency-Key", "must-not-confirm");
        confirm.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var confirmResponse = await client.SendAsync(confirm);
        Assert.Equal(HttpStatusCode.Forbidden, confirmResponse.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-7.10")]
    public async Task Return_binding_provider_mismatch_is_rejected_without_status_cookie()
    {
        using var factory = new ReturnFactory(NewCancelledPaidOrder(), NewSucceededTransaction(OrderId));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        using var response = await client.GetAsync("/api/v1/payment-returns/omise?state=return-token");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(response.Headers, header =>
            string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase));
    }

    private static Order NewCancelledPaidOrder()
    {
        var order = Order.CreateDraft(new OrderDraftInput(
            MerchantId,
            Guid.NewGuid(),
            "insurance",
            "THB",
            [new TrustedOrderLineInput(
                "DOC-1", "VMI", "Document", 1, Money.Of(100m, "THB"),
                Money.Zero("THB"), Money.Zero("THB"), Money.Of(100m, "THB"), "catalog")],
            Money.Zero("THB"), Money.Zero("THB"), null, null,
            DateTime.UtcNow, "ORD6900000301"));
        order.Issue(DateTime.UtcNow);
        order.Cancel(DateTime.UtcNow);
        order.ApplySuccessfulTransaction(TransactionId, DateTime.UtcNow);
        return order;
    }

    private static Transaction NewSucceededTransaction(Guid orderId)
    {
        var transaction = Transaction.Create(
            TransactionId, MerchantId, orderId, "TXN-RETURN", 1, Money.Of(100m, "THB"), "card",
            Code.TwoCTwoP, Guid.NewGuid(), PspEnvironment.Sandbox, Guid.NewGuid(), 1,
            "request-return", "{\"schemaVersion\":1}", DateTime.UtcNow);
        transaction.BindRedirect("charge-return", "https://psp.example/return", DateTime.UtcNow);
        transaction.SetReturnBinding("return-token");
        transaction.MarkSucceeded("charge-return", "paid", DateTime.UtcNow, needsReview: true,
            reviewCode: "paid_needs_review");
        return transaction;
    }

    private sealed class ReturnFactory : WebApplicationFactory<ApiHost::Program>
    {
        public readonly RecordingAdapter Adapter = new();

        public ReturnFactory(Order order, Transaction transaction)
        {
            Order = order;
            Transaction = transaction;
        }

        private Order Order { get; }
        private Transaction Transaction { get; }

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
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPspAdapterFactory>();
                services.RemoveAll<ITransactionReturnBindingService>();
                services.RemoveAll<ITransactionRepository>();
                services.RemoveAll<ICheckoutTransactionStore>();
                services.AddSingleton<ITransactionReturnBindingService>(
                    new FakeReturnBindingService(Transaction.Id, Order.Id, ExpiresAt));
                services.AddSingleton<IPspAdapterFactory>(new RecordingAdapterFactory(Adapter));
                services.AddSingleton<ITransactionRepository>(new FakeTransactionRepository(Transaction));
                services.AddSingleton<ICheckoutTransactionStore>(new FakeCheckoutTransactionStore(Order));
            });
        }
    }

    private sealed class FakeReturnBindingService(Guid transactionId, Guid orderId, DateTime expiresAt) : ITransactionReturnBindingService
    {
        public string Issue(Guid transactionId, Guid orderId, string browserBindingId, DateTime expiresAt) => "return-token";

        public bool TryRead(string value, out TransactionReturnBinding binding)
        {
            binding = new TransactionReturnBinding(transactionId, orderId, "tab-a", expiresAt);
            return value == "return-token";
        }
    }

    private sealed class FakeCheckoutTransactionStore(Order order) : ICheckoutTransactionStore
    {
        public Task<CheckoutTransactionContext?> GetCheckoutForUpdateAsync(
            Guid merchantId, Guid orderId, Guid linkId, CancellationToken cancellationToken) =>
            Task.FromResult<CheckoutTransactionContext?>(
                merchantId == MerchantId && orderId == order.Id ? new CheckoutTransactionContext(order, null) : null);
    }

    private sealed class FakeTransactionRepository(Transaction transaction) : ITransactionRepository
    {
        public void Add(Transaction value) { }
        public void AddEvent(TransactionEvent value) { }
        public Task<Transaction?> GetByIdAsync(Guid merchantId, Guid transactionId, CancellationToken cancellationToken) =>
            Task.FromResult<Transaction?>(merchantId == transaction.MerchantId && transactionId == transaction.Id ? transaction : null);
        public Task<PagedResult<Transaction>> ListAsync(Guid merchantId, int page, int limit, string? status, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<Transaction>(merchantId == transaction.MerchantId ? [transaction] : [], page, limit, merchantId == transaction.MerchantId ? 1 : 0));
        public Task<Transaction?> GetForUpdateTransactionAsync(Guid merchantId, Guid transactionId, CancellationToken cancellationToken) =>
            GetByIdAsync(merchantId, transactionId, cancellationToken);
        public Task<Transaction?> GetPotentialForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<Transaction?>(null);
        public Task<Transaction?> GetLatestForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<Transaction?>(transaction.OrderId == orderId ? transaction : null);
        public Task<Transaction?> GetByProviderReferenceAsync(Guid merchantId, Guid providerAccountId, PspEnvironment? environment, string reference, CancellationToken cancellationToken) =>
            Task.FromResult<Transaction?>(null);
        public Task<Transaction?> GetByReturnBindingAsync(string returnBinding, CancellationToken cancellationToken) =>
            Task.FromResult<Transaction?>(returnBinding == "return-token" ? transaction : null);
        public Task<IReadOnlyList<(Guid MerchantId, Guid TransactionId)>> ListDueAsync(DateTime now, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(Guid MerchantId, Guid TransactionId)>>([]);
        public Task<int> NextAttemptNoAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) => Task.FromResult(1);
        public Task<bool> EventExistsAsync(Guid merchantId, Guid transactionId, string source, string eventReference, CancellationToken cancellationToken) =>
            Task.FromResult(false);
        public Task<IReadOnlyList<TransactionEvent>> ListEventsAsync(Guid merchantId, Guid transactionId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TransactionEvent>>([]);
    }

    private sealed class RecordingAdapterFactory(IPspAdapter adapter) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => adapter;
    }

    private sealed class RecordingAdapter : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>(["card"]);
        public int CreateCalls { get; private set; }
        public int FetchCalls { get; private set; }
        public Task<PspCharge> CreateRedirectChargeAsync(Session session, Guid pspConnectionId, string secret,
            PspEnvironment environment, CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.FromResult(new PspCharge("unused", "https://unused.example"));
        }

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment,
            CancellationToken cancellationToken) => Task.FromResult(new PspProbeResult("ok", "unused"));

        public bool VerifyWebhook(string rawPayload, string signature, string secret) => false;
        public WebhookEvent ParseWebhook(string rawPayload) => new("unused", "unused", PspChargeStatus.Pending);
        public PspWebhookReference ExtractWebhookReference(string rawPayload) => new("unused", "unused");
        public Task<PspChargeConfirmation> FetchChargeAsync(string externalChargeId, string secret,
            PspEnvironment environment, CancellationToken cancellationToken)
        {
            FetchCalls++;
            return Task.FromResult(new PspChargeConfirmation(PspChargeStatus.Pending, null));
        }
    }
}
