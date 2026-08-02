extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ApiHost::Api.Merchants;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using SharedKernel;
using PaymentSession = Payments.Domain.Session;

namespace Hosts.Tests;

// purchase-flow-completion REQ-4 — POST /orders/{orderId}/cancel at the HTTP boundary: the release of the
// order's payment session and the cancel itself are two commands in two units of work, and this is the layer
// that proves the ORDER of them (a cancel granted while the session is still chargeable is money against a
// dead order). Same shape as MerchantLifecycleEndpointTests: the merchant-user policy is re-pointed at a fake
// scheme, the persistence ports are in-memory, and no DB is touched. The confirmation service resolves for
// real — every case here keeps the session chargeless, so it decides offline and never reaches a PSP.

file sealed class TestMerchantUserAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestMerchantUser";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "merchant-user-sub-1")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class BoundActor(Guid merchantId) : IActorContext
{
    public Guid MerchantId => merchantId;
    public Guid? UserId => Guid.Parse("44444444-4444-4444-4444-444444444444");
    public bool HasActor => true;
}

file sealed class FakeOrders(List<Order> orders) : IOrderRepository
{
    public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(orders.FirstOrDefault(o => o.Id == orderId));

    public Task<Order?> GetByCheckoutSessionIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    /// <summary>The last orderNo the list endpoint asked for — null means "no filter", which is a different
    /// thing from "filter that matched nothing" and is exactly what the SFS parse can get wrong.</summary>
    public static string? LastOrderNoFilter { get; private set; }

    public Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, string? orderNo, CancellationToken cancellationToken)
    {
        LastOrderNoFilter = orderNo;
        return Task.FromResult<IReadOnlyList<Order>>(
            orders.Where(o => orderNo is null || o.OrderNo == orderNo).ToList());
    }

    public void Add(Order order) => orders.Add(order);
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

file sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await operation(cancellationToken);
}

file sealed class OrderFactory(Guid merchantId, List<Order> orders, List<PaymentSession> sessions)
    : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestMerchantUserAuthHandler>(
                    TestMerchantUserAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("merchant-user", p => p
                .AddAuthenticationSchemes(TestMerchantUserAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            // Last-registered wins. The aggregates live in the lists, so a "save" is a no-op while mutations
            // survive across requests exactly as committed rows would.
            services.AddScoped<IActorContext>(_ => new BoundActor(merchantId));
            services.AddScoped<IOrderRepository>(_ => new FakeOrders(orders));
            services.AddScoped<ISessionRepository>(_ => new FakePaymentSessions(sessions));
            services.AddScoped<IUnitOfWork>(_ => new NoOpUnitOfWork());
        });
    }
}

public sealed class OrderCancelEndpointTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Money Amount = Money.Of(1200m, "THB");

    private static Order NewOrder() => Order.Create(
        Merchant, Amount, DateTime.UtcNow.AddHours(-1),
        [new OrderItemInput(
            Guid.NewGuid(), 1, Amount, "00098-69100/กธ/037677-10", "VMI", "POLICY", null, null, null,
            "Somchai", "Jaidee", "1234567890123", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc))], orderNo: "ORD6900000001");

    /// <summary>A chargeless session for <paramref name="order"/>: never redirected, so the confirmation
    /// service can settle it on the clock alone. <paramref name="age"/> past the TTL makes it releasable.</summary>
    private static PaymentSession SessionFor(Order order, TimeSpan age) => PaymentSession.Create(
        Merchant, order.Id, Amount, PaymentMethods.Card, Code.TwoCTwoP, DateTime.UtcNow - age);

    private static HttpRequestMessage Cancel(Guid orderId, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderId}/cancel");
        if (csrf)
        {
            request.Headers.Add("Cookie", $"{UserSessionCookies.CsrfCookieName}=tok-1");
            request.Headers.Add(UserCsrfFilter.HeaderName, "tok-1");
        }

        return request;
    }

    // REQ-4.1 — nothing is holding the order, so it cancels.
    [Fact]
    public async Task Cancelling_an_unpaid_order_with_no_payment_session_succeeds()
    {
        var order = NewOrder();
        using var factory = new OrderFactory(Merchant, [order], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Cancel(order.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    // REQ-4.2 — the previous attempt aged out: it is retired first, then the order is cancelled. Both effects
    // are asserted, because a cancel that skipped the release would look identical on the status code alone.
    [Fact]
    public async Task Cancelling_an_order_whose_session_has_aged_out_expires_the_session_first()
    {
        var order = NewOrder();
        var session = SessionFor(order, PaymentSession.OpenTtl + TimeSpan.FromHours(1));
        using var factory = new OrderFactory(Merchant, [order], [session]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Cancel(order.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SessionStatus.Expired, session.Status);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    // REQ-4.3 — the customer may be paying right now: 409, and the order MUST be left alone.
    [Fact]
    public async Task Cancelling_an_order_with_a_live_payment_session_is_409()
    {
        var order = NewOrder();
        var session = SessionFor(order, TimeSpan.FromMinutes(5));
        using var factory = new OrderFactory(Merchant, [order], [session]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Cancel(order.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Equal(SessionStatus.Created, session.Status);
    }

    // REQ-4.4 — money was collected; the cancel is refused.
    [Fact]
    public async Task Cancelling_a_paid_order_is_409()
    {
        var order = NewOrder();
        order.MarkPaid(Amount, DateTime.UtcNow);
        using var factory = new OrderFactory(Merchant, [order], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Cancel(order.Id));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(OrderStatus.Paid, order.Status);
    }

    // REQ-4.5 — a repeated cancel (double click, retry after a dropped response) succeeds unchanged.
    [Fact]
    public async Task Cancelling_the_same_order_twice_succeeds()
    {
        var order = NewOrder();
        using var factory = new OrderFactory(Merchant, [order], []);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Cancel(order.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Cancel(order.Id))).StatusCode);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }

    [Fact]
    public async Task Cancelling_an_unknown_order_is_404()
    {
        using var factory = new OrderFactory(Merchant, [], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Cancel(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // REQ-4.6 — merchant-user policy (faked here, it has to be) AND the CSRF filter. What this pins is the
    // filter: no token, no cancel.
    [Fact]
    public async Task Cancelling_without_a_CSRF_token_is_403()
    {
        var order = NewOrder();
        using var factory = new OrderFactory(Merchant, [order], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Cancel(order.Id, csrf: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
    }

    // ---- purchase-flow-completion REQ-7.3/7.4: GET /orders --------------------------------------------

    private static HttpRequestMessage Get(string path) => new(HttpMethod.Get, path);

    // REQ-7.4 — the SFS `filters` contract, adopted for orderNo/eq only. The parsed value has to reach the
    // repository: a filter that is dropped on the floor returns every order and looks like a working page.
    [Fact]
    public async Task Listing_orders_passes_the_orderNo_filter_through()
    {
        var order = NewOrder();
        using var factory = new OrderFactory(Merchant, [order], []);
        using var client = factory.CreateClient();

        var filters = Uri.EscapeDataString("""[{"field":"orderNo","operator":"eq","value":"ORD6900000001"}]""");
        var response = await client.SendAsync(Get($"/api/v1/orders?filters={filters}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ORD6900000001", FakeOrders.LastOrderNoFilter);
        Assert.Contains("ORD6900000001", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // No filters at all is "everything", not "match nothing".
    [Fact]
    public async Task Listing_orders_without_a_filter_asks_for_everything()
    {
        using var factory = new OrderFactory(Merchant, [NewOrder()], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/v1/orders"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(FakeOrders.LastOrderNoFilter);
    }

    // ---- purchase-flow-completion REQ-8.8: GET /payments/sessions/{id} --------------------------------

    // A session that is not there — or belongs to another company, which the query filter makes the same
    // thing — must be 404. It answered 409 before this task, because the handler raised
    // InvalidOperationException; the route mapping and that status are what this pins.
    [Fact]
    public async Task Reading_a_payment_session_that_does_not_exist_is_404()
    {
        using var factory = new OrderFactory(Merchant, [], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get($"/api/v1/payments/sessions/{Guid.NewGuid()}"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_payment_session_returns_its_status()
    {
        var order = NewOrder();
        var session = SessionFor(order, TimeSpan.FromMinutes(5));
        using var factory = new OrderFactory(Merchant, [order], [session]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get($"/api/v1/payments/sessions/{session.Id}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(SessionStatus.Created.ToString(), body, StringComparison.Ordinal);
    }

    // The SFS whitelist rule: a field or operator this surface does not support is dropped, not an error.
    [Theory]
    [InlineData("""[{"field":"status","operator":"eq","value":"Paid"}]""")]
    [InlineData("""[{"field":"orderNo","operator":"like","value":"ORD69%"}]""")]
    public async Task Listing_orders_silently_drops_a_filter_it_does_not_support(string filtersJson)
    {
        using var factory = new OrderFactory(Merchant, [NewOrder()], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get($"/api/v1/orders?filters={Uri.EscapeDataString(filtersJson)}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(FakeOrders.LastOrderNoFilter);
    }

    // Malformed SFS JSON is a client error, and must be a 400 — not the opaque 500 a BadHttpRequestException
    // would produce (the reason SfsQueryParser throws ArgumentException).
    [Fact]
    public async Task Listing_orders_with_malformed_filters_is_400()
    {
        using var factory = new OrderFactory(Merchant, [NewOrder()], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/v1/orders?filters=not-json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
