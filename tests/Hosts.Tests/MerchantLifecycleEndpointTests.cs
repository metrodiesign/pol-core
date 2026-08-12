extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ApiHost::Api.Merchants;
using BuildingBlocks.Application;
using Carts.Application;
using Iam.Domain.Permissions;
using Merchants.Application;
using Merchants.Application.Users;
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
using Orders.Application;
using OrderAggregate = Orders.Domain.Order;
using Cart = Carts.Domain.Cart;

namespace Hosts.Tests;

// merchant-commerce ERD reset — the merchant Cart-to-Order lifecycle at the HTTP
// boundary, driven through the REAL route + policy + CSRF filter + minimal-API lambda + mediator pipeline. The
// "merchant-user" policy is re-pointed at a fake always-present scheme and the persistence ports are in-memory
// fakes over shared lists, so no live DB is touched. The catalogue is read live from a fake ISpDocumentGateway
// and the sold-check is a fake IDocumentSaleProbe that
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

file sealed class BoundActor(Guid merchantId, string? saleCode) : IActorContext, IUserScope
{
    private static readonly Guid MerchantUserId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public Guid MerchantId => merchantId;
    public Guid? UserId => MerchantUserId;
    public bool HasActor => true;
    public string? SaleCode => saleCode;
    public bool IsBound => true;
    public Resolution Current => new(MerchantUserId, "merchant@test.local", merchantId,
        new HashSet<string>([Keys.PaymentView, Keys.PaymentCreate, Keys.PaymentRedirect], StringComparer.Ordinal), saleCode);
}

// Reports every document sellable — add-item/order sold-checks pass, so these tests exercise the rest of the
// orchestration. The sold-path 4xx are proven in the probe's own tests.
file sealed class AlwaysSellableProbe : IDocumentSaleProbe
{
    public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentSaleStatus>>([]);
}

file sealed class CountingSellableProbe : IDocumentSaleProbe
{
    public int Calls { get; private set; }
    public int LastKeyCount { get; private set; }

    public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken)
    {
        Calls++;
        LastKeyCount = keys.Count;
        return Task.FromResult<IReadOnlyList<DocumentSaleStatus>>([]);
    }
}

// Reports named documents already sold, so add-item and direct-order sold paths run.
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
file sealed class FakeCarts(List<Cart> carts) : ICartRepository, ICartForOrderStore
{
    public void Add(Cart cart) => carts.Add(cart);

    public Task<Cart?> GetAsync(Guid cartId, CancellationToken cancellationToken) =>
        Task.FromResult(carts.FirstOrDefault(c => c.Id == cartId));

    public Task<Cart?> ReloadTrackedAsync(Guid cartId, CancellationToken cancellationToken) =>
        GetAsync(cartId, cancellationToken);
}

file sealed class LifecycleOrderStore(List<OrderAggregate> orders) : IOrderStore
{
    public void Add(OrderAggregate order) => orders.Add(order);
}

file sealed class LifecycleOrderNoSequence : IOrderNoSequence
{
    private int _next;
    public Task<string> NextAsync(CancellationToken cancellationToken) =>
        Task.FromResult($"ORD69{++_next:D8}");
}

file sealed class LifecycleOutbox : IOutbox
{
    public List<Mediator.INotification> Events { get; } = [];
    public void Enqueue(Mediator.INotification notification) => Events.Add(notification);
}

file sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await operation(cancellationToken);
}

// The merchant's 2C2P connection. A null method list
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
    string? enabledMethods = "card,promptpay,installment",
    string? saleCode = MerchantLifecycleEndpointTests.SaleCode,
    IDocumentSaleProbe? saleProbe = null,
    List<OrderAggregate>? orders = null,
    LifecycleOutbox? orderOutbox = null)
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
            services.PostConfigure<PolicySchemeOptions>(
                ApiHost::Api.Iam.ConsoleSessionAuthentication.SchemeName,
                options => options.ForwardDefaultSelector = context =>
                {
                    context.Features.Set(new ApiHost::Api.Iam.SelectedConsoleAudience(
                        ApiHost::Api.Iam.ConsoleAudience.Merchant));
                    return TestMerchantUserAuthHandler.SchemeName;
                });
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("merchant-user", p => p
                .AddAuthenticationSchemes(TestMerchantUserAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            // Last-registered wins: the endpoints' live document read (gateway), the sold-check (probe) and
            // every Cart-to-Order write runs for real, with no DB behind it. Actor is bound (and carries a
            // sale code) so IMerchantScoped messages pass MerchantGuardBehavior and the catalogue gate passes.
            services.AddScoped<ISpDocumentGateway>(_ => new FakeSpDocumentGateway(document is null ? [] : [document]));
            services.AddScoped<IDocumentSaleProbe>(_ => saleProbe ?? new AlwaysSellableProbe());
            services.AddScoped<IActorContext>(_ => new BoundActor(merchantId, saleCode));
            services.AddScoped<IUserScope>(_ => new BoundActor(merchantId, saleCode));
            services.AddScoped<FakeCarts>(_ => new FakeCarts(carts));
            services.AddScoped<ICartRepository>(sp => sp.GetRequiredService<FakeCarts>());
            services.AddScoped<ICartForOrderStore>(sp => sp.GetRequiredService<FakeCarts>());
            services.AddScoped<IUnitOfWork>(_ => new NoOpUnitOfWork());
            services.AddScoped<IOrderStore>(_ => new LifecycleOrderStore(orders ?? []));
            services.AddScoped<IOrderNoSequence>(_ => new LifecycleOrderNoSequence());
            services.AddScoped<IOutbox>(_ => orderOutbox ?? new LifecycleOutbox());
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

    private static CommerceItemMetadata MetadataFor(SpDocumentItem document) => new(
        CommerceItemMetadataCodec.InsuranceDocumentSource,
        document.DocumentType,
        document.PolicyNumber,
        document.StartDate is { } start ? DateOnly.FromDateTime(start) : null,
        document.EndDate is { } end ? DateOnly.FromDateTime(end) : null);

    private static Cart CartWith(SpDocumentItem document)
    {
        var cart = new Cart(Guid.CreateVersion7(), Merchant, SaleCode, Now);
        cart.AddItem(document.DocumentNo!, document.SaleCode!, document.SourceSystem!,
            document.ShowName ?? document.SourceSystem, 1, Money.Of(document.TotalPremium!.Value, "THB"),
            MetadataFor(document));
        return cart;
    }

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

    private static string AddItemBody(string productCode = DocumentNo, string variantCode = Group, int quantity = 1) =>
        $$"""{"productCode":"{{productCode}}","variantCode":"{{variantCode}}","quantity":{{quantity}}}""";

    private static string CreateOrderBody(Cart cart, string? amount = null) =>
        $$"""
        {"cartId":"{{cart.Id}}",
         "customer":{"name":"Somchai Jaidee","phone":"0812345678","email":"buyer@example.com"},
         "paymentMethod":"card"
         {{(amount is null ? "" : $",\"amount\":{amount}")}}}
        """;

    [Fact]
    public async Task Creating_order_from_cart_returns_201_and_commits_generic_snapshot()
    {
        var document = UnpaidDocument();
        var cart = new Cart(Guid.CreateVersion7(), Merchant, SaleCode, Now);
        cart.AddItem(document.DocumentNo!, document.SaleCode!, document.SourceSystem!, Group, 2,
            Money.Of(document.TotalPremium!.Value, "THB"), MetadataFor(document));
        var orders = new List<OrderAggregate>();
        var outbox = new LifecycleOutbox();
        var probe = new CountingSellableProbe();
        using var factory = new CartFactory(
            Merchant, document, [cart], saleProbe: probe, orders: orders, orderOutbox: outbox);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/orders", CreateOrderBody(cart)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var order = Assert.Single(orders);
        Assert.Equal($"/api/v1/orders/{order.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Pending", payload.GetProperty("status").GetString());
        Assert.Equal("2400.0000", payload.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal(Carts.Domain.CartStatus.CheckedOut, cart.Status);
        Assert.Equal(SaleCode, order.SaleCode);
        Assert.Equal("card", order.PaymentChannel);
        var line = Assert.Single(order.Items);
        Assert.Equal(DocumentNo, line.ProductCode);
        Assert.Equal(Group, line.VariantCode);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(Money.Zero("THB"), line.Discount);
        Assert.Equal(CommerceItemMetadataCodec.InsuranceDocumentSource,
            CommerceItemMetadataCodec.Parse(line.Metadata!).SourceType);
        Assert.Single(outbox.Events);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, probe.LastKeyCount);
    }

    [Fact]
    public async Task Creating_order_rejects_claimed_amount_mismatch_without_writes()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var orders = new List<OrderAggregate>();
        var outbox = new LifecycleOutbox();
        using var factory = new CartFactory(Merchant, document, [cart], orders: orders, orderOutbox: outbox);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            "/api/v1/orders", CreateOrderBody(cart, "{\"amount\":\"999.0000\",\"currency\":\"THB\"}")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(orders);
        Assert.Empty(outbox.Events);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);
    }

    [Fact]
    public async Task Creating_order_from_checked_out_cart_or_retry_is_409()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var orders = new List<OrderAggregate>();
        using var factory = new CartFactory(Merchant, document, [cart], orders: orders);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created,
            (await client.SendAsync(Post("/api/v1/orders", CreateOrderBody(cart)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(Post("/api/v1/orders", CreateOrderBody(cart)))).StatusCode);
        Assert.Single(orders);
    }

    [Fact]
    public async Task Creating_order_requires_sale_code_and_csrf()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        using var noSaleFactory = new CartFactory(Merchant, document, [cart], saleCode: null);
        using var noSaleClient = noSaleFactory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await noSaleClient.SendAsync(Post("/api/v1/orders", CreateOrderBody(cart)))).StatusCode);

        using var factory = new CartFactory(Merchant, document, [CartWith(document)]);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Post("/api/v1/orders", CreateOrderBody(cart), csrf: false))).StatusCode);
    }

    [Fact]
    public async Task Creating_order_rejects_sold_product_with_409()
    {
        var document = UnpaidDocument();
        var cart = CartWith(document);
        var orders = new List<OrderAggregate>();
        using var factory = new CartFactory(
            Merchant, document, [cart], saleProbe: new SoldProbe(DocumentNo), orders: orders);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/orders", CreateOrderBody(cart)));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(orders);
        Assert.Equal(Carts.Domain.CartStatus.Open, cart.Status);
    }

    // REQ-5.3 — a document the upstream reports PAID is already sold, so POST /carts/{id}/items refuses it
    // with 400 at the endpoint, before any cart write is attempted.
    [Fact]
    public async Task Adding_a_document_that_is_PAID_upstream_is_rejected_with_400()
    {
        var sold = Doc("00098-69100/กธ/037676-10", paymentStatus: "PAID", paidDate: new DateTime(2026, 7, 15));

        using var factory = new CartFactory(Merchant, sold, []);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            $"/api/v1/carts/{Guid.NewGuid()}/items", AddItemBody(sold.DocumentNo!)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // REQ-4.5 — a document the upstream does not return cannot be added: 400.
    [Fact]
    public async Task Adding_an_unknown_document_is_400()
    {
        using var factory = new CartFactory(Merchant, UnpaidDocument(), []);
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
        var cart = new Cart(Guid.CreateVersion7(), Merchant, SaleCode, Now);
        var probe = new CountingSellableProbe();
        using var factory = new CartFactory(Merchant, document, [cart], saleProbe: probe);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/carts/{cart.Id}/items", AddItemBody(quantity: 3)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var line = Assert.Single(cart.Items);
        Assert.Equal(DocumentNo, line.ProductCode);
        Assert.Equal(SaleCode, line.SaleCode);
        Assert.Equal(Group, line.VariantCode);
        Assert.Equal(Group, line.VariantName);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(Money.Of(1200m, "THB"), line.UnitPrice);
        Assert.Equal(Money.Of(3600m, "THB"), line.LineTotal);
        Assert.Equal(CommerceItemMetadataCodec.Serialize(MetadataFor(document)), line.Metadata);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(1, probe.LastKeyCount);

        var read = await client.GetAsync($"/api/v1/carts/{cart.Id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var payload = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(SaleCode, payload.RootElement.GetProperty("saleCode").GetString());
        var wireLine = Assert.Single(payload.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(DocumentNo, wireLine.GetProperty("productCode").GetString());
        Assert.Equal(Group, wireLine.GetProperty("variantCode").GetString());
        Assert.Equal(3, wireLine.GetProperty("quantity").GetInt32());
        Assert.Equal(CommerceItemMetadataCodec.InsuranceDocumentSource,
            wireLine.GetProperty("metadata").GetProperty("sourceType").GetString());
        Assert.False(wireLine.TryGetProperty("documentNo", out _));
        Assert.False(wireLine.TryGetProperty("productGroup", out _));
    }

    [Fact]
    public async Task Add_item_rejects_client_price_or_metadata_fields()
    {
        var document = UnpaidDocument();
        var cart = new Cart(Guid.CreateVersion7(), Merchant, SaleCode, Now);
        var probe = new CountingSellableProbe();
        using var factory = new CartFactory(Merchant, document, [cart], saleProbe: probe);
        using var client = factory.CreateClient();

        var invalidQuantity = await client.SendAsync(Post(
            $"/api/v1/carts/{cart.Id}/items", AddItemBody(quantity: 0)));
        Assert.Equal(HttpStatusCode.BadRequest, invalidQuantity.StatusCode);
        var body = $$$"""
            {"productCode":"{{{DocumentNo}}}","variantCode":"{{{Group}}}","quantity":1,
             "unitPrice":{"amount":"0.01","currency":"THB"},"metadata":{"insuredName":"leak"}}
            """;

        var response = await client.SendAsync(Post($"/api/v1/carts/{cart.Id}/items", body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(cart.Items);
        Assert.Equal(0, probe.Calls);
    }

    // REQ-5.4 — the probe reports the document already sold (or mid-payment) on this platform, so POST
    // /carts/{id}/items refuses it with 400 before any cart write, and the message names no other merchant
    // or order (REQ-5.7).
    [Fact]
    public async Task Adding_a_document_already_sold_on_the_platform_is_rejected_with_400()
    {
        var document = UnpaidDocument();
        var cart = new Cart(Guid.CreateVersion7(), Merchant, SaleCode, Now);
        using var factory = new CartFactory(Merchant, document, [cart], saleProbe: new SoldProbe(DocumentNo));
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
        using var factory = new CartFactory(Merchant, UnpaidDocument(), [], saleCode: null);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("sale-code-missing", problem.RootElement.GetProperty("code").GetString());
    }

    // REQ-4.9 — the sale-code gate runs BEFORE the body is parsed: an add-item request with a product group that
    // would otherwise be a 400 still comes back 403 when no sale code is bound, so 403 wins the ordering.
    [Fact]
    public async Task Adding_an_item_without_a_bound_sale_code_is_403_before_the_body_is_parsed()
    {
        using var factory = new CartFactory(Merchant, UnpaidDocument(), [], saleCode: null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post(
            $"/api/v1/carts/{Guid.NewGuid()}/items", AddItemBody(variantCode: "not-a-group")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("sale-code-missing", problem.RootElement.GetProperty("code").GetString());
    }

}
