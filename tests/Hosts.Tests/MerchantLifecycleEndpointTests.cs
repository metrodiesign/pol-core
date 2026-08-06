extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using ApiHost::Api.Merchants;
using BuildingBlocks.Application;
using Carts.Application;
using Checkouts.Application;
using Checkouts.Domain.Items;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;
using Products.Application.Ports;
using SharedKernel;
using Cart = Carts.Domain.Cart;
using CheckoutSession = Checkouts.Domain.Session;

namespace Hosts.Tests;

// purchase-flow-completion + products-external-source-of-truth — the merchant cart/checkout lifecycle at the HTTP
// boundary, driven through the REAL route + policy + CSRF filter + minimal-API lambda + mediator pipeline. The
// "merchant-user" policy is re-pointed at a fake always-present scheme and the persistence ports are in-memory
// fakes over shared lists, so no live DB is touched. The catalogue is read live from a fake ISpDocumentGateway
// (add-item and per-line checkout both look the document up) and the sold-check is a fake IDocumentSaleProbe that
// reports every document sellable: what is under test here is the ORCHESTRATION, not EF or the upstream.

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

file sealed class BoundActor(Guid merchantId, string? saleCode) : IActorContext
{
    public Guid MerchantId => merchantId;
    public Guid? UserId => null;
    public bool HasActor => true;
    public string? SaleCode => saleCode;
}

// Reports every document sellable — checkout/add-item sold-checks pass, so these tests exercise the rest of the
// orchestration. The sold-path 4xx are proven in the probe's own tests.
file sealed class AlwaysSellableProbe : IDocumentSaleProbe
{
    public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentSaleStatus>>([]);
}

// Reports the named documents already sold on this platform, so the add-item (400, REQ-5.4) and checkout
// (409, REQ-5.5) sold-paths actually run — the always-sellable default can never enter those branches.
file sealed class SoldProbe(params string[] soldDocumentNos) : IDocumentSaleProbe
{
    private readonly HashSet<string> _sold = new(soldDocumentNos, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentSaleStatus>>(
            [.. keys.Where(k => _sold.Contains(k.DocumentNo))
                .Select(k => new DocumentSaleStatus(k, DocumentSaleState.Sold, Guid.NewGuid()))]);
}

// The aggregates live in the lists, so a "save" is a no-op and mutations survive across requests exactly as
// committed rows would.
file sealed class FakeCarts(List<Cart> carts) : ICartRepository
{
    public void Add(Cart cart) => carts.Add(cart);

    public Task<Cart?> GetAsync(Guid cartId, CancellationToken cancellationToken) =>
        Task.FromResult(carts.FirstOrDefault(c => c.Id == cartId));
}

file sealed class FakeCheckouts(List<CheckoutSession> sessions) : ICheckoutRepository
{
    public void Add(CheckoutSession session) => sessions.Add(session);

    public Task<CheckoutSession?> GetByIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.FirstOrDefault(s => s.Id == checkoutSessionId));

    public Task<CheckoutSession?> GetOpenForCartAsync(Guid cartId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.FirstOrDefault(s => s.CartId == cartId
            && s.Status is Checkouts.Domain.SessionStatus.Started or Checkouts.Domain.SessionStatus.Confirmed));
}

file sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await operation(cancellationToken);
}

// The merchant's 2C2P connection, as the checkout eligibility gate reads it (REQ-6.1). A null method list
// stands for a merchant with no connection at all. The adapter behind the gate is the REAL TwoCTwoPAdapter
// the host registers — SupportedMethods needs no HTTP — so these tests see the production capability set.
file sealed class FakeConnections(Guid merchantId, string? enabledMethods, DateTime now) : IConnectionRepository
{
    private readonly Connection? _connection = enabledMethods is null
        ? null
        : Connection.Create(merchantId, Code.TwoCTwoP, enabledMethods, "psp/test/2c2p", now);

    public Task<Connection?> GetAsync(Guid merchant, Code psp, CancellationToken cancellationToken) =>
        Task.FromResult(_connection?.MerchantId == merchant && _connection.Psp == psp ? _connection : null);

    public Task<Connection?> GetByIdAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(_connection?.Id == connectionId ? _connection : null);

    public void Add(Connection connection) => throw new NotSupportedException();

    public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchant, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Connection>>(_connection is null ? [] : [_connection]);
}

file sealed class CartFactory(
    Guid merchantId,
    SpDocumentItem? document,
    List<Cart> carts,
    List<CheckoutSession> sessions,
    string? enabledMethods = "card,promptpay,installment",
    string? saleCode = MerchantLifecycleEndpointTests.SaleCode,
    IDocumentSaleProbe? saleProbe = null)
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
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestMerchantUserAuthHandler>(
                    TestMerchantUserAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("merchant-user", p => p
                .AddAuthenticationSchemes(TestMerchantUserAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            // Last-registered wins: the endpoints' live document read (gateway), the sold-check (probe) and
            // every cart/checkout write run for real, with no DB behind them. The actor is bound (and carries a
            // sale code) so IMerchantScoped messages pass MerchantGuardBehavior and the catalogue gate passes.
            services.AddScoped<ISpDocumentGateway>(_ => new FakeSpDocumentGateway(document is null ? [] : [document]));
            services.AddScoped<IDocumentSaleProbe>(_ => saleProbe ?? new AlwaysSellableProbe());
            services.AddScoped<IActorContext>(_ => new BoundActor(merchantId, saleCode));
            services.AddScoped<ICartRepository>(_ => new FakeCarts(carts));
            services.AddScoped<ICheckoutRepository>(_ => new FakeCheckouts(sessions));
            services.AddScoped<IUnitOfWork>(_ => new NoOpUnitOfWork());
            services.AddScoped<IConnectionRepository>(_ => new FakeConnections(
                merchantId, enabledMethods, new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc)));
        });
    }
}

public sealed class MerchantLifecycleEndpointTests
{
    internal const string SaleCode = "00098";
    internal const string DocumentNo = "00098-69100/กธ/037677-10";
    internal const string Group = "VMI";
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>An upstream §5.2 row for the catalogue document (products-external-source-of-truth REQ-3.5).</summary>
    private static SpDocumentItem Doc(string documentNo = DocumentNo, string paymentStatus = "UNPAID",
        decimal totalPremium = 1200m, DateTime? paidDate = null) =>
        new("Motor", Group, "POLICY", documentNo,
            null, null, null, null, null, null, null, null,
            SaleCode, null, null, null, null, null, null, null,
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31), null,
            null, null, null, totalPremium, null, null, paidDate,
            null, paymentStatus);

    private static SpDocumentItem UnpaidDocument() => Doc();

    private static Cart CartWith(SpDocumentItem document)
    {
        var cart = new Cart(Guid.CreateVersion7(), Merchant, Now);
        cart.AddItem(document.DocumentNo!, document.SaleCode!, document.SourceSystem!, 1,
            Money.Of(document.TotalPremium!.Value, "THB"));
        return cart;
    }

    private static CheckoutSession SessionFor(Cart cart, SpDocumentItem document) => CheckoutSession.Start(
        Merchant, cart.Id, cart.Subtotal!.Value, Now,
        [new CheckoutItemInput(
            1, Money.Of(document.TotalPremium!.Value, "THB"),
            document.DocumentNo!, document.SourceSystem!, document.DocumentType!,
            document.PolicyNumber, document.StartDate, document.EndDate,
            "Somchai", "Jaidee", "1234567890123", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc))],
        Checkouts.Domain.PaymentChannel.CARD, CustomerContact.Of("Somchai Jaidee", "0812345678", null));

    private static HttpRequestMessage Post(string path, string? json = null, bool csrf = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (csrf)
        {
            request.Headers.Add("Cookie", $"{UserSessionCookies.CsrfCookieName}=tok-1");
            request.Headers.Add(UserCsrfFilter.HeaderName, "tok-1");
        }

        return request;
    }

    private static string AddItemBody(string documentNo = DocumentNo, string productGroup = Group, int quantity = 1) =>
        $$"""{"documentNo":"{{documentNo}}","productGroup":"{{productGroup}}","quantity":{{quantity}}}""";

    private static string StartCheckoutBody(Cart cart, SpDocumentItem document, string channel = "CARD", decimal? discount = null) =>
        $$"""
        {"cartId":"{{cart.Id}}","paymentChannel":"{{channel}}",
         "customer":{"name":"Somchai Jaidee","phone":"0812345678","email":"buyer@example.com"},
         "insuredPersons":[
          {"documentNo":"{{document.DocumentNo}}","firstName":"Somchai","lastName":"Jaidee",
           "idNumber":"1234567890123","dateOfBirth":"1990-01-01T00:00:00Z"
           {{(discount is null ? "" : $",\"discount\":{discount}")}}}]}
        """;

    // REQ-5.3 — a document the upstream reports PAID is already sold, so POST /carts/{id}/items refuses it
    // with 400 at the endpoint, before any cart write is attempted.
    [Fact]
    public async Task Adding_a_document_that_is_PAID_upstream_is_rejected_with_400()
    {
        var sold = Doc("00098-69100/กธ/037676-10", paymentStatus: "PAID", paidDate: new DateTime(2026, 7, 15));

        using var factory = new CartFactory(Merchant, sold, [], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            $"/api/v1/carts/{Guid.NewGuid()}/items", AddItemBody(sold.DocumentNo!)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // REQ-4.5 — a document the upstream does not return cannot be added: 400.
    [Fact]
    public async Task Adding_an_unknown_document_is_400()
    {
        using var factory = new CartFactory(Merchant, UnpaidDocument(), [], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            $"/api/v1/carts/{Guid.NewGuid()}/items", AddItemBody("no-such-document")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // REQ-4.1/4.2/4.3/4.7 — a successful add reads the document live and prices the line from the upstream's
    // TotalPremium (never the client), mints THB here, and stores the upstream's own DocumentNo/SaleCode/
    // ProductGroup. This is the ONLY test that drives POST /carts/{id}/items to a 200, so it is the one that
    // proves the endpoint's happy path at all.
    [Fact]
    public async Task Adding_a_document_prices_the_line_from_the_upstream_premium_and_stores_its_own_values()
    {
        var document = UnpaidDocument();   // TotalPremium 1200, SaleCode 00098, group VMI
        var cart = new Cart(Guid.CreateVersion7(), Merchant, Now);
        using var factory = new CartFactory(Merchant, document, [cart], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/carts/{cart.Id}/items", AddItemBody()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(cart.Items);
        Assert.Equal(DocumentNo, line.DocumentNo);          // REQ-4.7 — the upstream's own returned values
        Assert.Equal(SaleCode, line.SaleCode);
        Assert.Equal(Group, line.ProductGroup);
        Assert.Equal(Money.Of(1200m, "THB"), line.UnitPrice);   // REQ-4.1/4.2/4.3 — priced from TotalPremium, THB minted here
    }

    // REQ-5.4 — the probe reports the document already sold (or mid-payment) on this platform, so POST
    // /carts/{id}/items refuses it with 400 before any cart write, and the message names no other merchant
    // or order (REQ-5.7).
    [Fact]
    public async Task Adding_a_document_already_sold_on_the_platform_is_rejected_with_400()
    {
        var document = UnpaidDocument();
        var cart = new Cart(Guid.CreateVersion7(), Merchant, Now);
        using var factory = new CartFactory(Merchant, document, [cart], [], saleProbe: new SoldProbe(DocumentNo));
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/carts/{cart.Id}/items", AddItemBody()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(cart.Items);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Merchant.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    // REQ-4.9 — a merchant user with no sale code bound has no catalogue access at all: GET /products is 403.
    [Fact]
    public async Task Listing_products_without_a_bound_sale_code_is_403()
    {
        using var factory = new CartFactory(Merchant, UnpaidDocument(), [], [], saleCode: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // REQ-4.9 — the sale-code gate runs BEFORE the body is parsed: an add-item request with a product group that
    // would otherwise be a 400 still comes back 403 when no sale code is bound, so 403 wins the ordering.
    [Fact]
    public async Task Adding_an_item_without_a_bound_sale_code_is_403_before_the_body_is_parsed()
    {
        using var factory = new CartFactory(Merchant, UnpaidDocument(), [], [], saleCode: null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            $"/api/v1/carts/{Guid.NewGuid()}/items", AddItemBody(productGroup: "not-a-group")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // REQ-2.1 — starting a checkout freezes the cart. Two units of work behind one request: the session is
    // created, then MarkCartCheckedOutCommand flips the cart.
    [Fact]
    public async Task Starting_a_checkout_freezes_the_cart()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        using var factory = new CartFactory(Merchant, document, [cart], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Carts.Domain.CartStatus.CheckedOut, cart.Status);
    }

    // REQ-2.2 — the frozen cart cannot start a second checkout.
    [Fact]
    public async Task Starting_a_checkout_on_a_frozen_cart_is_409()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, document, [cart], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // A cart that somehow stayed Open while a live session exists: the handler's GetOpenForCartAsync pre-check
    // catches it, and the ConflictException it throws must surface as the SAME 409, not a 500.
    [Fact]
    public async Task Starting_a_checkout_while_a_live_session_exists_is_409()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        using var factory = new CartFactory(Merchant, document, [cart], [SessionFor(cart, document)]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // REQ-5.5 — a cart line whose document the probe reports sold (or mid-payment) on this platform stops the
    // whole checkout with 409 before any session is created; the message names no other merchant or order (REQ-5.7).
    [Fact]
    public async Task Starting_a_checkout_whose_document_is_already_sold_is_409()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions, saleProbe: new SoldProbe(DocumentNo));
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(sessions);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Merchant.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    // REQ-7.4 — a cart document that no longer comes back from the upstream at checkout (e.g. it fell out of the
    // 6-month search window while in the cart) is a 409, and no session is created.
    [Fact]
    public async Task Starting_a_checkout_whose_document_the_upstream_no_longer_returns_is_409()
    {
        var inCart = Doc("00098-69100/กธ/999999-10");   // in the cart, but the gateway below does not carry it
        var cart = CartWith(inCart);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, UnpaidDocument(), [cart], sessions);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, inCart)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(sessions);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);
    }

    // REQ-5.3 (checkout half) — the upstream reports the cart's document PAID at checkout time, so it can never be
    // sold again: 409, and no session is created.
    [Fact]
    public async Task Starting_a_checkout_whose_document_the_upstream_reports_PAID_is_409()
    {
        var inCart = UnpaidDocument();                                    // priced into the cart while UNPAID
        var cart = CartWith(inCart);
        var paidUpstream = Doc(DocumentNo, paymentStatus: "PAID", paidDate: new DateTime(2026, 7, 15));
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, paidUpstream, [cart], sessions);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, inCart)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(sessions);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);
    }

    // REQ-2.7 — every cart mutation on a frozen cart is a 409 at the wire, not a 500: the domain guard
    // throws InvalidOperationException and the shared ProblemDetails handler maps it.
    [Theory]
    [InlineData("POST", "items")]
    [InlineData("DELETE", "item")]
    [InlineData("PUT", "item")]
    [InlineData("POST", "clear")]
    public async Task Mutating_a_frozen_cart_is_409(string method, string shape)
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, document, [cart], []);
        using var client = factory.CreateClient();

        var (path, json) = shape switch
        {
            "items" => ($"/api/v1/carts/{cart.Id}/items", AddItemBody()),
            "item" => ($"/api/v1/carts/{cart.Id}/items/{Guid.NewGuid()}", """{"quantity":2}"""),
            _ => ($"/api/v1/carts/{cart.Id}/clear", (string?)null),
        };

        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (json is not null && method != "DELETE")
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("Cookie", $"{UserSessionCookies.CsrfCookieName}=tok-1");
        request.Headers.Add(UserCsrfFilter.HeaderName, "tok-1");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // REQ-2.5 — abandon is the way back: the session goes Abandoned, the cart reopens, and the merchant can
    // check out again. This is the whole point of the cycle, so it is asserted end to end.
    [Fact]
    public async Task Abandoning_a_checkout_reopens_the_cart_and_lets_it_start_a_new_one()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var session = SessionFor(cart, document);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, document, [cart], [session]);
        using var client = factory.CreateClient();

        var abandon = await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"));

        Assert.Equal(HttpStatusCode.OK, abandon.StatusCode);
        Assert.Equal(Checkouts.Domain.SessionStatus.Abandoned, session.Status);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);

        var restart = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document)));
        Assert.Equal(HttpStatusCode.OK, restart.StatusCode);
    }

    // REQ-2.9 — a repeated cancel (double click, retry after a dropped response) succeeds unchanged.
    [Fact]
    public async Task Abandoning_the_same_checkout_twice_succeeds()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var session = SessionFor(cart, document);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, document, [cart], [session]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"))).StatusCode);
    }

    // REQ-2.6 — a confirmed checkout has already become an order; cancelling it is the order flow, not this.
    [Fact]
    public async Task Abandoning_a_confirmed_checkout_is_409()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var session = SessionFor(cart, document);
        session.Confirm();
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, document, [cart], [session]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Carts.Domain.CartStatus.CheckedOut, cart.Status);
    }

    [Fact]
    public async Task Abandoning_an_unknown_checkout_is_404()
    {
        using var factory = new CartFactory(Merchant, UnpaidDocument(), [], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/checkouts/{Guid.NewGuid()}/abandon"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // REQ-2.8 — the abandon endpoint is gated by the merchant-user policy AND the CSRF filter. The policy is
    // faked here (it has to be), so what this pins is the filter: no token, no cancel.
    [Fact]
    public async Task Abandoning_a_checkout_without_a_CSRF_token_is_403()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var session = SessionFor(cart, document);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, document, [cart], [session]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon", csrf: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(Checkouts.Domain.SessionStatus.Started, session.Status);
    }

    // ---- purchase-flow-completion REQ-6: what POST /checkouts now takes, and what it refuses --------------

    // REQ-6.1/6.6 — the channel and the buyer's contact ride the request onto the session.
    [Theory]
    [InlineData("CARD", Checkouts.Domain.PaymentChannel.CARD)]
    [InlineData("PROMPTPAY_QR", Checkouts.Domain.PaymentChannel.PROMPTPAY_QR)]
    [InlineData("INSTALLMENT", Checkouts.Domain.PaymentChannel.INSTALLMENT)]
    public async Task Starting_a_checkout_records_the_channel_and_the_customer(
        string wire, Checkouts.Domain.PaymentChannel expected)
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document, wire)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = Assert.Single(sessions);
        Assert.Equal(expected, session.Channel);
        Assert.Equal("Somchai Jaidee", session.CustomerName);
        Assert.Equal("0812345678", session.CustomerPhone);
        Assert.Equal("buyer@example.com", session.CustomerEmail);
    }

    // REQ-6.2 — anything outside the three supported channels is a 400, and nothing is written.
    [Theory]
    [InlineData("BITCOIN")]
    [InlineData("card")]        // the wire values are the enum member names, case included
    [InlineData("0")]           // the underlying number is not a wire value
    [InlineData("2")]
    [InlineData("CARD,INSTALLMENT")]    // nor is a comma list Enum.TryParse would happily accept
    [InlineData("")]
    public async Task Starting_a_checkout_with_an_unsupported_channel_is_400(string channel)
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document, channel)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sessions);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);
    }

    // REQ-6.1 — a channel the platform supports but THIS merchant's connection does not enable is refused at
    // start checkout, not left to fail when the customer finally clicks pay.
    [Theory]
    [InlineData("PROMPTPAY_QR")]
    [InlineData("INSTALLMENT")]
    public async Task Starting_a_checkout_on_a_channel_the_merchant_cannot_be_charged_on_is_400(string channel)
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions, enabledMethods: "card");
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document, channel)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sessions);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);

        // …while a channel that connection DOES enable still goes through, so the gate is reading the
        // merchant's method list rather than refusing everything.
        var card = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document, "CARD")));
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
    }

    // REQ-6.1 — no PSP connection at all means no channel is chargeable: same 400, no order to strand.
    [Fact]
    public async Task Starting_a_checkout_for_a_merchant_with_no_connection_is_400()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions, enabledMethods: null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, document)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sessions);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);
    }

    // REQ-8.5 — a documentNo repeated in insuredPersons is a 400 before anything is written.
    [Fact]
    public async Task Starting_a_checkout_with_a_duplicate_insured_documentNo_is_400()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var body = $$"""
        {"cartId":"{{cart.Id}}","paymentChannel":"CARD",
         "customer":{"name":"Somchai Jaidee","phone":"0812345678"},
         "insuredPersons":[
          {"documentNo":"{{DocumentNo}}","firstName":"A","lastName":"A","idNumber":"1111111111111","dateOfBirth":"1990-01-01T00:00:00Z"},
          {"documentNo":"{{DocumentNo}}","firstName":"B","lastName":"B","idNumber":"2222222222222","dateOfBirth":"1990-01-01T00:00:00Z"}]}
        """;

        var response = await client.SendAsync(Post("/api/v1/checkouts", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sessions);
    }

    // REQ-6.7 — a missing name/phone or a malformed phone/email is a 400 before anything is written.
    [Theory]
    [InlineData(null)]
    [InlineData("""{"name":"","phone":"0812345678"}""")]
    [InlineData("""{"name":"Somchai Jaidee","phone":""}""")]
    [InlineData("""{"name":"Somchai Jaidee","phone":"12"}""")]
    [InlineData("""{"name":"Somchai Jaidee","phone":"0812345678","email":"not-an-email"}""")]
    public async Task Starting_a_checkout_with_an_invalid_customer_is_400(string? customerJson)
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var body = $$"""
        {"cartId":"{{cart.Id}}","paymentChannel":"CARD","customer":{{customerJson ?? "null"}},
         "insuredPersons":[
          {"documentNo":"{{DocumentNo}}","firstName":"Somchai","lastName":"Jaidee",
           "idNumber":"1234567890123","dateOfBirth":"1990-01-01T00:00:00Z"}]}
        """;

        var response = await client.SendAsync(Post("/api/v1/checkouts", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sessions);
    }

    // REQ-6.3 — the discount comes off the line, and the session is priced at the net total.
    [Fact]
    public async Task A_line_discount_is_subtracted_from_the_checkout_total()
    {
        var document = UnpaidDocument();   // 1200 THB
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Post("/api/v1/checkouts", StartCheckoutBody(cart, document, discount: 200m)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var session = Assert.Single(sessions);
        Assert.Equal(Money.Of(1000m, "THB"), session.Amount);
        Assert.Equal(Money.Of(200m, "THB"), Assert.Single(session.Items).Discount);
    }

    // REQ-6.4 — a discount that exceeds its line, or a negative one, never reaches the session.
    [Theory]
    [InlineData(1200.01)]
    [InlineData(-1)]
    public async Task An_out_of_range_discount_is_400(decimal discount)
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(
            Post("/api/v1/checkouts", StartCheckoutBody(cart, document, discount: discount)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sessions);
    }

    // REQ-6.5 — a client total that disagrees with the server's arithmetic is rejected, not silently repriced.
    [Fact]
    public async Task A_client_total_that_disagrees_with_the_server_is_400()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var body = StartCheckoutBody(cart, document, discount: 200m)
            .Replace("\"cartId\"", "\"amount\":1200,\"cartId\"", StringComparison.Ordinal);

        var response = await client.SendAsync(Post("/api/v1/checkouts", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task A_client_total_that_matches_the_net_is_accepted()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var sessions = new List<CheckoutSession>();
        using var factory = new CartFactory(Merchant, document, [cart], sessions);
        using var client = factory.CreateClient();

        var body = StartCheckoutBody(cart, document, discount: 200m)
            .Replace("\"cartId\"", "\"amount\":1000,\"cartId\"", StringComparison.Ordinal);

        var response = await client.SendAsync(Post("/api/v1/checkouts", body));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Money.Of(1000m, "THB"), Assert.Single(sessions).Amount);
    }
}
