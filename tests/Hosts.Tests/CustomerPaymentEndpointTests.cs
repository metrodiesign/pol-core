extern alias ApiHost;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orders.Application;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;
using PaymentSession = Payments.Domain.Session;

namespace Hosts.Tests;

// purchase-flow-completion REQ-8 — the customer's two anonymous endpoints at the HTTP boundary:
// POST /orders/{token}/pay and POST /orders/{token}/payment-status. What only this layer can prove is the
// composition: the summary token resolves the order, the ORDER's merchant is bound into the actor scope
// (nothing in the request carries it), the channel the merchant chose becomes the method actually charged,
// and every refusal comes back as 404 or 409 with no hint about which. No cookie and no CSRF token is ever
// sent — the token IS the capability (REQ-8.1) — and no DB is touched: the persistence ports and the PSP
// are in-memory, while the mediator pipeline, the routes and the confirmation service are the real ones.

file sealed class FakeCustomerSummaryReader(OrderSummary? summary) : IOrderSummaryReader
{
    public const string Token = "tok-customer-1";

    public Task<OrderSummary?> GetByTokenAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult(token == Token ? summary : null);
}

file sealed class FakePayableOrders(PayableOrder? order) : IPayableOrderReader
{
    public Task<PayableOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(order?.OrderId == orderId ? order : null);

    // The in-memory order cannot change between the two reads, so the locked re-read answers the same row.
    public Task<PayableOrder?> GetForMintAsync(Guid orderId, CancellationToken cancellationToken) =>
        GetAsync(orderId, cancellationToken);

    public Task AttachAttemptAsync(
        Guid orderId, Guid paymentSessionId, string method, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    // These tests do not exercise the pre-charge sold-check, so the order carries no document keys.
    public Task<IReadOnlyList<BuildingBlocks.Application.DocumentKey>> GetDocumentKeysAsync(
        Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BuildingBlocks.Application.DocumentKey>>([]);
}

file sealed class FakePaymentSessions(List<PaymentSession> sessions) : ISessionRepository
{
    public void Add(PaymentSession session) => sessions.Add(session);

    public Task<PaymentSession?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.FirstOrDefault(s => s.Id == paymentSessionId));

    public Task<PaymentSession?> GetByExternalChargeAsync(Code psp, string externalChargeId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PaymentSession?> GetOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.FirstOrDefault(s =>
            s.OrderId == orderId && s.Status is SessionStatus.Created or SessionStatus.Redirected));
}

file sealed class FakeConnections(Guid merchantId, string? enabledMethods) : IConnectionRepository
{
    private readonly Connection? _connection = enabledMethods is null
        ? null
        : Connection.Create(
            merchantId, Code.TwoCTwoP, enabledMethods, "psp/test/2c2p",
            new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    public Task<Connection?> GetAsync(Guid merchant, Code psp, CancellationToken cancellationToken) =>
        Task.FromResult(_connection?.MerchantId == merchant && _connection.Psp == psp ? _connection : null);

    public Task<Connection?> GetByIdAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(_connection?.Id == connectionId ? _connection : null);

    public void Add(Connection connection) => throw new NotSupportedException();

    public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchant, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Connection>>(_connection is null ? [] : [_connection]);
}

/// <summary>Stands in for 2C2P. <see cref="Fetched"/> is what proves a short-circuit: a status check that
/// answered from the order must never have asked.</summary>
file sealed class FakeCustomerPspAdapter(PspChargeStatus fetchStatus, Money fetchAmount) : IPspAdapter
{
    public Code Psp => Code.TwoCTwoP;

    public IReadOnlySet<string> SupportedMethods =>
        new HashSet<string>([PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment], StringComparer.Ordinal);

    public int Charges { get; private set; }

    public int Fetched { get; private set; }

    public Task<PspCharge> CreateRedirectChargeAsync(
        PaymentSession session, Guid pspConnectionId, string secret, CancellationToken cancellationToken)
    {
        Charges++;
        return Task.FromResult(new PspCharge($"INV-{Charges}", $"https://2c2p.test/hosted/{Charges}"));
    }

    public bool VerifyWebhook(string rawPayload, string signature, string secret) => throw new NotSupportedException();

    public Task<PspChargeConfirmation> FetchChargeAsync(string externalChargeId, string secret, CancellationToken cancellationToken)
    {
        Fetched++;
        return Task.FromResult(new PspChargeConfirmation(fetchStatus, fetchAmount));
    }

    public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
}

file sealed class FakeAdapterFactory(IPspAdapter adapter) : IPspAdapterFactory
{
    public IPspAdapter For(Code psp) => adapter;
}

file sealed class FakeVault : IVaultSecretStore
{
    public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
        Task.FromResult("psp-test-secret");

    public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

file sealed class FakeIdempotency : IIdempotencyStore
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public Task<bool> TryBeginAsync(IReadOnlyCollection<string> keys, string context, CancellationToken cancellationToken)
    {
        if (keys.Any(k => _seen.Contains($"{context}:{k}")))
            return Task.FromResult(false);

        foreach (var key in keys)
            _seen.Add($"{context}:{key}");

        return Task.FromResult(true);
    }
}

file sealed class FakeOutbox(List<INotification> enqueued) : IOutbox
{
    public void Enqueue(INotification notification) => enqueued.Add(notification);
}

file sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await operation(cancellationToken);
}

file sealed class CustomerFactory(
    OrderSummary? summary,
    PayableOrder? order,
    List<PaymentSession> sessions,
    IPspAdapter adapter,
    List<INotification> published,
    string? enabledMethods = "card,promptpay,installment")
    : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
        builder.ConfigureServices(services =>
        {
            // Last-registered wins. Deliberately NO IActorContext override: the point of these endpoints is
            // that the host binds the order's merchant through the real IActorScope, exactly like the
            // webhook, and a fake actor would hide a missing binding.
            services.AddScoped<IOrderSummaryReader>(_ => new FakeCustomerSummaryReader(summary));
            services.AddScoped<IPayableOrderReader>(_ => new FakePayableOrders(order));
            services.AddScoped<ISessionRepository>(_ => new FakePaymentSessions(sessions));
            services.AddScoped<IConnectionRepository>(_ => new FakeConnections(
                summary?.MerchantId ?? Guid.Empty, enabledMethods));
            services.AddScoped<IPspAdapterFactory>(_ => new FakeAdapterFactory(adapter));
            services.AddScoped<IVaultSecretStore>(_ => new FakeVault());
            services.AddScoped<IIdempotencyStore>(_ => new FakeIdempotency());
            services.AddScoped<IOutbox>(_ => new FakeOutbox(published));
            services.AddScoped<IUnitOfWork>(_ => new NoOpUnitOfWork());
        });
    }
}

public sealed class CustomerPaymentEndpointTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Order = Guid.NewGuid();
    private static readonly Money Amount = Money.Of(15000m, "THB");

    private static OrderSummary Summary(
        string status = "Pending", string? channel = "CARD", TimeSpan? expiresIn = null) =>
        new(Order, Merchant, "ORD6900000001", Amount, status, channel,
            DateTime.UtcNow + (expiresIn ?? TimeSpan.FromHours(24)),
            [new OrderSummaryLine("00098-69100/กธ/900001-10", "VMI", "ประกันรถยนต์", 1, Amount)]);

    private static PayableOrder Payable(PayableOrderStatus status = PayableOrderStatus.Pending) =>
        new(Order, Amount, status);

    private static HttpRequestMessage Post(string action) =>
        new(HttpMethod.Post, $"/api/v1/orders/{FakeCustomerSummaryReader.Token}/{action}");

    // --- POST /orders/{token}/pay ---------------------------------------------------------------------

    // REQ-8.1 — the whole path, with no cookie and no CSRF header on the request at all.
    [Fact]
    public async Task Paying_from_the_link_returns_the_hosted_redirect_url()
    {
        var adapter = new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount);
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(Summary(), Payable(), sessions, adapter, []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://2c2p.test/hosted/1", json.GetProperty("redirectUrl").GetString());
        Assert.Single(sessions);
        // REQ-8.4 — the merchant came from the order and stays there.
        Assert.Equal(Merchant, sessions[0].MerchantId);
        Assert.False(json.TryGetProperty("merchantId", out _));
    }

    // Customer payment uses canonical method attached to Order, never a default.
    [Theory]
    [InlineData("card", PaymentMethods.Card)]
    [InlineData("promptpay", PaymentMethods.PromptPay)]
    [InlineData("installment", PaymentMethods.Installment)]
    public async Task Paying_charges_through_the_attached_method(string channel, string method)
    {
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(
            Summary(channel: channel), Payable(), sessions, new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(method, sessions[0].Method);
    }

    // REQ-8.10 — double click / second tab. One session, one charge, the same URL back.
    [Fact]
    public async Task Paying_twice_resumes_the_same_charge()
    {
        var adapter = new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount);
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(Summary(), Payable(), sessions, adapter, []);
        using var client = factory.CreateClient();

        var first = await client.SendAsync(Post("pay"));
        var second = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
        Assert.Single(sessions);
        Assert.Equal(1, adapter.Charges);
    }

    // REQ-8.2 — a token that does not exist and one that has expired are the SAME answer; neither confirms
    // that the other kind of token would have worked.
    [Fact]
    public async Task Paying_with_an_unknown_token_is_404()
    {
        using var factory = new CustomerFactory(
            Summary(), Payable(), [], new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders/no-such-token/pay"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Paying_with_an_expired_token_is_404_not_410()
    {
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(
            Summary(expiresIn: TimeSpan.FromHours(-1)), Payable(), sessions,
            new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(sessions);
    }

    // REQ-8.11 — a cancelled order's link is dead (404), a paid one is a conflict the customer can act on.
    [Fact]
    public async Task Paying_a_cancelled_order_is_404()
    {
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(
            Summary(status: "Cancelled"), Payable(PayableOrderStatus.Cancelled), sessions,
            new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task Paying_an_already_paid_order_is_409()
    {
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(
            Summary(status: "Paid"), Payable(PayableOrderStatus.Paid), sessions,
            new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(sessions);
    }

    // Direct Order with no attached attempt has no safe default to charge through.
    [Fact]
    public async Task Paying_an_order_with_no_payment_channel_is_409()
    {
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(
            Summary(channel: null), Payable(), sessions,
            new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(sessions);
    }

    // No 2C2P connection for this merchant: 409 generic, and nothing about which merchant or which PSP.
    [Fact]
    public async Task Paying_when_the_merchant_has_no_usable_connection_is_409()
    {
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(
            Summary(), Payable(), sessions, new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), [],
            enabledMethods: null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("pay"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(sessions);
    }

    // --- POST /orders/{token}/payment-status ------------------------------------------------------------

    [Fact]
    public async Task The_status_of_an_order_with_no_attempt_is_pending()
    {
        var adapter = new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount);
        using var factory = new CustomerFactory(Summary(), Payable(), [], adapter, []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("payment-status"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("pending", json.GetProperty("status").GetString());
        Assert.Equal(0, adapter.Fetched);
    }

    // REQ-8.12 — answered from the order, and the short-circuit is the assertion on Fetched: a paid order
    // polled every few seconds must not cost a PSP inquiry each time.
    [Fact]
    public async Task The_status_of_a_paid_order_is_paid_without_asking_the_PSP()
    {
        var adapter = new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount);
        using var factory = new CustomerFactory(
            Summary(status: "Paid"), Payable(PayableOrderStatus.Paid), [], adapter, []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("payment-status"));

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("paid", json.GetProperty("status").GetString());
        Assert.Equal(0, adapter.Fetched);
    }

    [Fact]
    public async Task The_status_of_a_cancelled_order_is_cancelled()
    {
        var adapter = new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount);
        using var factory = new CustomerFactory(
            Summary(status: "Cancelled"), Payable(PayableOrderStatus.Cancelled), [], adapter, []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("payment-status"));

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("cancelled", json.GetProperty("status").GetString());
        Assert.Equal(0, adapter.Fetched);
    }

    // REQ-8.5/8.6 — the customer is back from 2C2P before the webhook. The status check settles it on the
    // same confirm line: the session goes Paid and PaymentPaid is published exactly once.
    [Fact]
    public async Task The_status_check_settles_a_charge_the_webhook_has_not_reported_yet()
    {
        var adapter = new FakeCustomerPspAdapter(PspChargeStatus.Paid, Amount);
        List<PaymentSession> sessions = [];
        List<INotification> published = [];
        using var factory = new CustomerFactory(Summary(), Payable(), sessions, adapter, published);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post("pay"))).StatusCode);
        var response = await client.SendAsync(Post("payment-status"));

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("paid", json.GetProperty("status").GetString());
        Assert.Equal(SessionStatus.Paid, sessions[0].Status);
        Assert.Single(published);
    }

    [Fact]
    public async Task The_status_of_a_refused_charge_is_failed()
    {
        var adapter = new FakeCustomerPspAdapter(PspChargeStatus.Failed, Amount);
        List<PaymentSession> sessions = [];
        using var factory = new CustomerFactory(Summary(), Payable(), sessions, adapter, []);
        using var client = factory.CreateClient();

        await client.SendAsync(Post("pay"));
        var response = await client.SendAsync(Post("payment-status"));

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("failed", json.GetProperty("status").GetString());
        Assert.Equal(SessionStatus.Failed, sessions[0].Status);
    }

    [Fact]
    public async Task Asking_for_the_status_with_an_expired_token_is_404()
    {
        using var factory = new CustomerFactory(
            Summary(expiresIn: TimeSpan.FromHours(-1)), Payable(), [],
            new FakeCustomerPspAdapter(PspChargeStatus.Pending, Amount), []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("payment-status"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
