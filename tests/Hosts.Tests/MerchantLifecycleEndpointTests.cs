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
using Products.Application;
using Products.Domain;
using SharedKernel;
using Cart = Carts.Domain.Cart;
using CheckoutSession = Checkouts.Domain.Session;

namespace Hosts.Tests;

// purchase-flow-completion — the merchant cart/checkout lifecycle at the HTTP boundary, driven through the
// REAL route + policy + CSRF filter + minimal-API lambda + mediator pipeline (the layer
// InsuranceCheckoutEndToEndTests, which calls handlers directly, cannot reach). The "merchant-user" policy is
// re-pointed at a fake always-present scheme (same trick as RegistrationHistoryEndpointTests) and the three
// persistence ports are in-memory fakes over shared lists, so no live DB is touched: what is under test here
// is the ORCHESTRATION (which commands the endpoints send, in what order, and which status the resulting
// exception becomes), not EF.

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

file sealed class OneProductRepository(Product? product) : IProductRepository
{
    public void Add(Product product) => throw new NotSupportedException();

    public Task<IReadOnlyList<Product>> UpsertByDocumentNoAsync(
        IReadOnlyList<ProductInput> inputs, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(product);
}

file sealed class BoundActor(Guid merchantId) : IActorContext
{
    public Guid MerchantId => merchantId;
    public Guid? UserId => null;
    public bool HasActor => true;
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

file sealed class CartFactory(Guid merchantId, Product? product, List<Cart> carts, List<CheckoutSession> sessions)
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

            // Last-registered wins, for all four: the gates' product read and every cart/checkout write run
            // for real, with no DB behind them. The actor is bound so IMerchantScoped messages pass
            // MerchantGuardBehavior — the fake auth scheme carries no merchant claim of its own.
            services.AddScoped<IProductRepository>(_ => new OneProductRepository(product));
            services.AddScoped<IActorContext>(_ => new BoundActor(merchantId));
            services.AddScoped<ICartRepository>(_ => new FakeCarts(carts));
            services.AddScoped<ICheckoutRepository>(_ => new FakeCheckouts(sessions));
            services.AddScoped<IUnitOfWork>(_ => new NoOpUnitOfWork());
        });
    }
}

public sealed class MerchantLifecycleEndpointTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);

    private static Product UnpaidProduct() => Product.Create(new ProductInput(
        ProductGroup.VMI, DocumentType.POLICY, "00098-69100/กธ/037677-10", "00098", 1200m,
        PaymentStatus.UNPAID, null,
        StartDate: new DateTime(2026, 7, 1), EndDate: new DateTime(2026, 7, 31)));

    private static Cart CartWith(Product product)
    {
        var cart = new Cart(Guid.CreateVersion7(), Merchant, Now);
        cart.AddItem(product.Id, 1, Money.Of(product.TotalPremium, "THB"));
        return cart;
    }

    private static CheckoutSession SessionFor(Cart cart, Product product) => CheckoutSession.Start(
        Merchant, cart.Id, cart.Subtotal!.Value, Now,
        [new CheckoutItemInput(
            product.Id, 1, Money.Of(product.TotalPremium, "THB"),
            product.DocumentNo, product.ProductGroup.ToString(), product.DocumentType.ToString(),
            product.PolicyNumber, product.StartDate, product.EndDate,
            "Somchai", "Jaidee", "1234567890123", new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc))]);

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

    private static string StartCheckoutBody(Cart cart, Product product) =>
        $$"""
        {"cartId":"{{cart.Id}}","recipient":"buyer@example.com","insuredPersons":[
          {"productId":"{{product.Id}}","firstName":"Somchai","lastName":"Jaidee",
           "idNumber":"1234567890123","dateOfBirth":"1990-01-01T00:00:00Z"}]}
        """;

    // REQ-1.3 — a document that is no longer UNPAID is already sold, so POST /carts/{id}/items refuses it
    // with 400 at the endpoint, before any cart write is attempted. Removing or widening the gate makes the
    // request fall through to AddItemToCartCommand and stop being a 400.
    [Fact]
    public async Task Adding_a_product_that_is_not_UNPAID_is_rejected_with_400()
    {
        var sold = Product.Create(new ProductInput(
            ProductGroup.VMI, DocumentType.POLICY, "00098-69100/กธ/037676-10", "00098", 1200m,
            PaymentStatus.PAID, new DateTime(2026, 7, 15)));

        using var factory = new CartFactory(Merchant, sold, [], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            $"/api/v1/carts/{Guid.NewGuid()}/items", $$"""{"productId":"{{sold.Id}}","quantity":1}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // REQ-2.1 — starting a checkout freezes the cart. Two units of work behind one request: the session is
    // created, then MarkCartCheckedOutCommand flips the cart.
    [Fact]
    public async Task Starting_a_checkout_freezes_the_cart()
    {
        var product = UnpaidProduct();
        var cart = CartWith(product);
        using var factory = new CartFactory(Merchant, product, [cart], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, product)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Carts.Domain.CartStatus.CheckedOut, cart.Status);
    }

    // REQ-2.2 — the frozen cart cannot start a second checkout.
    [Fact]
    public async Task Starting_a_checkout_on_a_frozen_cart_is_409()
    {
        var product = UnpaidProduct();
        var cart = CartWith(product);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, product, [cart], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, product)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // REQ-2.3 — and neither can a cart that somehow stayed Open while a live session exists (the freeze in
    // the second unit of work never landed): the handler's GetOpenForCartAsync pre-check catches it, and the
    // ConflictException it throws must surface as the SAME 409, not a 500.
    [Fact]
    public async Task Starting_a_checkout_while_a_live_session_exists_is_409()
    {
        var product = UnpaidProduct();
        var cart = CartWith(product);
        using var factory = new CartFactory(Merchant, product, [cart], [SessionFor(cart, product)]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, product)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
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
        var product = UnpaidProduct();
        var cart = CartWith(product);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, product, [cart], []);
        using var client = factory.CreateClient();

        var (path, json) = shape switch
        {
            "items" => ($"/api/v1/carts/{cart.Id}/items", $$"""{"productId":"{{product.Id}}","quantity":1}"""),
            "item" => ($"/api/v1/carts/{cart.Id}/items/{product.Id}", """{"quantity":2}"""),
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
        var product = UnpaidProduct();
        var cart = CartWith(product);
        var session = SessionFor(cart, product);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, product, [cart], [session]);
        using var client = factory.CreateClient();

        var abandon = await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"));

        Assert.Equal(HttpStatusCode.OK, abandon.StatusCode);
        Assert.Equal(Checkouts.Domain.SessionStatus.Abandoned, session.Status);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);

        var restart = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart, product)));
        Assert.Equal(HttpStatusCode.OK, restart.StatusCode);
    }

    // REQ-2.9 — a repeated cancel (double click, retry after a dropped response) succeeds unchanged.
    [Fact]
    public async Task Abandoning_the_same_checkout_twice_succeeds()
    {
        var product = UnpaidProduct();
        var cart = CartWith(product);
        var session = SessionFor(cart, product);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, product, [cart], [session]);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"))).StatusCode);
    }

    // REQ-2.6 — a confirmed checkout has already become an order; cancelling it is the order flow, not this.
    [Fact]
    public async Task Abandoning_a_confirmed_checkout_is_409()
    {
        var product = UnpaidProduct();
        var cart = CartWith(product);
        var session = SessionFor(cart, product);
        session.Confirm();
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, product, [cart], [session]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(Carts.Domain.CartStatus.CheckedOut, cart.Status);
    }

    [Fact]
    public async Task Abandoning_an_unknown_checkout_is_404()
    {
        using var factory = new CartFactory(Merchant, UnpaidProduct(), [], []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/checkouts/{Guid.NewGuid()}/abandon"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // REQ-2.8 — the abandon endpoint is gated by the merchant-user policy AND the CSRF filter. The policy is
    // faked here (it has to be), so what this pins is the filter: no token, no cancel.
    [Fact]
    public async Task Abandoning_a_checkout_without_a_CSRF_token_is_403()
    {
        var product = UnpaidProduct();
        var cart = CartWith(product);
        var session = SessionFor(cart, product);
        cart.MarkCheckedOut();
        using var factory = new CartFactory(Merchant, product, [cart], [session]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/checkouts/{session.Id}/abandon", csrf: false));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(Checkouts.Domain.SessionStatus.Started, session.Status);
    }
}
