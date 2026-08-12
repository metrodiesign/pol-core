extern alias ApiHost;

using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
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
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using Products.Application.Ports;
using SharedKernel;
using Cart = Carts.Domain.Cart;
using PaymentSession = Payments.Domain.Session;

namespace Hosts.Tests;

/// <summary>One captured log entry with its STRUCTURED properties — REQ-4.2 is asserted at the property
/// (design M2), never at the rendered message.</summary>
internal sealed record LogEntry(LogLevel Level, string Category, string Message,
    IReadOnlyDictionary<string, object?> Properties, Exception? Exception);

// probe-dependency-failure-mapping — the two behavioral suites of design.md "Testing Strategy":
//
//   Suite 1 (DB DEAD): the host boots with ConnectionStrings:App pointed at a fast-failing endpoint, so
//   every REAL Persistence.MerchantRuntime read on the money path hits a genuine SqlException. Every active
//   doors must answer THE SAME 503 (REQ-1.1/1.2), whose body carries no SQL text, server name, order id or
//   document number (REQ-3.1/3.2/3.4), and the handler's log line must carry the structured {ExceptionType}
//   property that separates "our DB is down" from "the upstream is down" (REQ-4.1/4.2/4.4).
//
//   Suite 2 (DB ALIVE, probe fails): a dead DB can prove nothing about residual state — you cannot count
//   rows in it (spec-architect B2) — so the sold-check is replaced by a fake that throws the new exception
//   while every store is an observable in-memory fake: no cart line is added, no PSP call is made and no
//   payment-session row is written.

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

/// <summary>The real merchant-user auth handler is replaced above, so the real IUserScope never binds —
/// this fake carries the permission keys the payment endpoints gate on.</summary>
file sealed class FakeUserScope(Guid merchantId, params string[] permissions) : IUserScope
{
    public bool IsBound => true;
    public Resolution Current { get; } =
        new(Guid.NewGuid(), "user@merchant.test", merchantId, new HashSet<string>(permissions, StringComparer.Ordinal));
}

/// <summary>Throws the classified failure exactly where the real probe would when VCentralPay is down.</summary>
file sealed class ThrowingProbe : IDocumentSaleProbe
{
    public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
        IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken) =>
        throw new DependencyUnavailableException(
            "A platform database read failed (SQL error 0, state 0, class 20).",
            new TimeoutException("simulated dead VCentralPay"));
}

file sealed class FakeCarts(List<Cart> carts) : ICartRepository
{
    public void Add(Cart cart) => carts.Add(cart);

    public Task<Cart?> GetAsync(Guid cartId, CancellationToken cancellationToken) =>
        Task.FromResult(carts.FirstOrDefault(c => c.Id == cartId));
}

file sealed class FakePayableOrders(PayableOrder? order, DocumentKey[] keys) : IPayableOrderReader
{
    public Task<PayableOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(order?.OrderId == orderId ? order : null);

    public Task<PayableOrder?> GetForMintAsync(Guid orderId, CancellationToken cancellationToken) =>
        GetAsync(orderId, cancellationToken);

    public Task AttachAttemptAsync(
        Guid orderId, Guid paymentSessionId, string method, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<DocumentKey>> GetDocumentKeysAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentKey>>(keys);
}

file sealed class FakePaymentSessions(List<PaymentSession> sessions) : ISessionRepository
{
    public void Add(PaymentSession session) => sessions.Add(session);

    public Task<PaymentSession?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.FirstOrDefault(s => s.Id == paymentSessionId));

    public Task<PagedResult<PaymentSession>> ListAsync(PagedQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new PagedResult<PaymentSession>(sessions, query.Page, query.Limit, sessions.Count));

    public Task<PaymentSession?> GetByExternalChargeAsync(Code psp, string externalChargeId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<PaymentSession?> GetOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.FirstOrDefault(s =>
            s.OrderId == orderId && s.Status is SessionStatus.Created or SessionStatus.Redirected));
}

file sealed class FakeConnections(Guid merchantId, string enabledMethods) : IConnectionRepository
{
    private readonly Connection _connection = Connection.Create(
        merchantId, Code.TwoCTwoP, enabledMethods, "psp/test/2c2p",
        new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));

    public Task<Connection?> GetAsync(Guid merchant, Code psp, CancellationToken cancellationToken) =>
        Task.FromResult(_connection.MerchantId == merchant && _connection.Psp == psp ? _connection : null);

    public Task<Connection?> GetByIdAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Task.FromResult(_connection.Id == connectionId ? _connection : null);

    public void Add(Connection connection) => throw new NotSupportedException();

    public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchant, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Connection>>([_connection]);
}

/// <summary>Counts charges — REQ-2.4's "no PSP call" is this staying at zero.</summary>
file sealed class CountingPspAdapter : IPspAdapter
{
    public Code Psp => Code.TwoCTwoP;

    public IReadOnlySet<string> SupportedMethods =>
        new HashSet<string>([PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment], StringComparer.Ordinal);

    public int Charges { get; private set; }

    public Task<PspCharge> CreateRedirectChargeAsync(
        PaymentSession session, Guid pspConnectionId, string secret, CancellationToken cancellationToken)
    {
        Charges++;
        return Task.FromResult(new PspCharge("INV-1", "https://2c2p.test/hosted/1"));
    }

    public bool VerifyWebhook(string rawPayload, string signature, string secret) => throw new NotSupportedException();

    public Task<PspChargeConfirmation> FetchChargeAsync(string externalChargeId, string secret, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
}

file sealed class FakeAdapterFactory(IPspAdapter adapter) : IPspAdapterFactory
{
    public IPspAdapter For(Code psp) => adapter;
}

file sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        await operation(cancellationToken);
}

file sealed class StructuredCapturingLoggerProvider(ConcurrentQueue<LogEntry> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var properties = state is IReadOnlyList<KeyValuePair<string, object?>> kvps
                ? kvps.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                : new Dictionary<string, object?>();
            sink.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), properties, exception));
        }
    }
}

/// <summary>Suite 1's host: real repositories over a DEAD VCentralPay (fast-failing endpoint), live fake
/// upstream (so the doors get PAST the gateway and fail at the platform read), faked auth + permission.</summary>
file sealed class DeadDbFactory(Guid merchantId, SpDocumentItem document, ConcurrentQueue<LogEntry> logSink)
    : WebApplicationFactory<ApiHost::Program>
{
    public const string FastFailConn = "Server=127.0.0.1,1;Database=pol_test;Connect Timeout=1;TrustServerCertificate=True";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        // UseSetting, not ConfigureAppConfiguration: Program.cs reads these at build time, and a developer's
        // local appsettings.Development.json would otherwise win (host-test-config-precedence lesson).
        builder.UseSetting("ConnectionStrings:App", FastFailConn);
        builder.UseSetting("ConnectionStrings:Admin", FastFailConn);
        builder.ConfigureLogging(logging => logging.AddProvider(new StructuredCapturingLoggerProvider(logSink)));
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

            services.AddScoped<IActorContext>(_ => new BoundActor(merchantId, PlatformDependencyFailureEndpointTests.SaleCode));
            services.AddScoped<IUserScope>(_ => new FakeUserScope(merchantId, Keys.PaymentCreate, Keys.PaymentView));
            // The upstream is ALIVE — the doors must fail at OUR platform read, not at the gateway.
            services.AddScoped<ISpDocumentGateway>(_ => new FakeSpDocumentGateway(document));
            // Everything else (carts, orders, sessions, probe) stays the host's REAL registration
            // over the dead connection string.
        });
    }
}

/// <summary>Suite 2's host: live in-memory stores, dead PROBE only.</summary>
file sealed class FakeProbeFactory(
    Guid merchantId,
    SpDocumentItem document,
    List<Cart> carts,
    List<PaymentSession> paymentSessions,
    CountingPspAdapter adapter,
    PayableOrder? payableOrder = null,
    DocumentKey[]? documentKeys = null)
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

            services.AddScoped<IActorContext>(_ => new BoundActor(merchantId, PlatformDependencyFailureEndpointTests.SaleCode));
            services.AddScoped<IUserScope>(_ => new FakeUserScope(merchantId, Keys.PaymentCreate, Keys.PaymentView));
            services.AddScoped<ISpDocumentGateway>(_ => new FakeSpDocumentGateway(document));
            services.AddScoped<IDocumentSaleProbe>(_ => new ThrowingProbe());
            services.AddScoped<ICartRepository>(_ => new FakeCarts(carts));
            services.AddScoped<IPayableOrderReader>(_ => new FakePayableOrders(payableOrder, documentKeys ?? []));
            services.AddScoped<ISessionRepository>(_ => new FakePaymentSessions(paymentSessions));
            services.AddScoped<IConnectionRepository>(_ => new FakeConnections(merchantId, "card,promptpay,installment"));
            services.AddScoped<IPspAdapterFactory>(_ => new FakeAdapterFactory(adapter));
            services.AddScoped<IUnitOfWork>(_ => new NoOpUnitOfWork());
        });
    }
}

public sealed class PlatformDependencyFailureEndpointTests
{
    internal const string SaleCode = "00098";
    private const string DocumentNo = "00098-69100/กธ/037677-10";
    private const string Group = "VMI";
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc);

    private static SpDocumentItem Doc() =>
        new("Motor", Group, "POLICY", DocumentNo,
            null, null, null, null, null, null, null, null,
            SaleCode, null, null, null, null, null, null, null,
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31), null,
            null, null, null, 1200m, null, null, null,
            null, "UNPAID");

    // --- Suite 1: DB dead -> one uniform 503 on every door, leak-free body, classified log --------------

    [Fact]
    public async Task Listing_products_with_a_dead_platform_db_is_503_with_a_leak_free_body()
    {
        var logs = new ConcurrentQueue<LogEntry>();
        using var factory = new DeadDbFactory(Merchant, Doc(), logs);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/v1/products?productFilters=" + Uri.EscapeDataString("""{"insuranceType":"Motor"}"""));

        await AssertDependencyUnavailableAsync(response);
        AssertClassifiedErrorLog(logs);
    }

    [Fact]
    public async Task Adding_a_cart_item_with_a_dead_platform_db_is_503_with_a_leak_free_body()
    {
        var logs = new ConcurrentQueue<LogEntry>();
        using var factory = new DeadDbFactory(Merchant, Doc(), logs);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/carts/{Guid.NewGuid()}/items", AddItemBody()));

        await AssertDependencyUnavailableAsync(response);
        AssertClassifiedErrorLog(logs);
    }

    [Fact]
    public async Task Creating_a_payment_session_with_a_dead_platform_db_is_503_with_a_leak_free_body()
    {
        var logs = new ConcurrentQueue<LogEntry>();
        using var factory = new DeadDbFactory(Merchant, Doc(), logs);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/payments/sessions", CreateSessionBody(Guid.NewGuid())));

        await AssertDependencyUnavailableAsync(response);
        AssertClassifiedErrorLog(logs);
    }

    // --- Suite 2: DB alive, probe fails -> the request leaves NO state behind ---------------------------

    [Fact]
    public async Task A_failed_sold_check_adds_no_cart_line()
    {
        var cart = new Cart(Guid.CreateVersion7(), Merchant, SaleCode, Now);
        using var factory = new FakeProbeFactory(Merchant, Doc(), [cart], [], new CountingPspAdapter());
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post($"/api/v1/carts/{cart.Id}/items", AddItemBody()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(cart.Items);   // REQ-2.2 — the request left the cart exactly as it found it
    }

    [Fact]
    public async Task A_failed_sold_check_makes_no_psp_call_and_writes_no_payment_session()
    {
        var orderId = Guid.NewGuid();
        var adapter = new CountingPspAdapter();
        var sessions = new List<PaymentSession>();
        using var factory = new FakeProbeFactory(
            Merchant, Doc(), [], sessions, adapter,
            payableOrder: new PayableOrder(orderId, Money.Of(1200m, "THB"), PayableOrderStatus.Pending),
            documentKeys: [new DocumentKey(DocumentNo, Group)]);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Post("/api/v1/payments/sessions", CreateSessionBody(orderId)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(sessions);        // REQ-2.4 — no session row
        Assert.Equal(0, adapter.Charges);   // REQ-2.4 — the PSP was never asked to mint a charge
    }

    // --- shared assertions ------------------------------------------------------------------------------

    /// <summary>503 + the fixed wire (same title as the upstream arm) + none of the internals REQ-3 bans:
    /// SQL/exception text, server or database name, connection string, order id, document number.</summary>
    private static async Task AssertDependencyUnavailableAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Upstream dependency unavailable", body);
        Assert.DoesNotContain("SqlException", body);
        Assert.DoesNotContain("SQL error", body);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("pol_test", body);
        Assert.DoesNotContain("Server=", body);
        Assert.DoesNotContain(DocumentNo, body);                       // REQ-3.4
        Assert.DoesNotContain(Merchant.ToString(), body, StringComparison.OrdinalIgnoreCase);   // REQ-3.2
    }

    /// <summary>REQ-4.1/4.2/4.4: an Error-level entry whose STRUCTURED {ExceptionType} property (not the
    /// rendered text — design M2) says DependencyUnavailableException, with the real exception attached and
    /// no credential in the message.</summary>
    private static void AssertClassifiedErrorLog(ConcurrentQueue<LogEntry> logs)
    {
        var entry = logs.Single(l =>
            l.Level == LogLevel.Error
            && l.Properties.TryGetValue("ExceptionType", out var type)
            && (string?)type?.ToString() == nameof(DependencyUnavailableException));
        Assert.NotNull(entry.Exception);
        Assert.IsType<DependencyUnavailableException>(entry.Exception);
        Assert.All(logs, l => Assert.DoesNotContain("Password=", l.Message, StringComparison.OrdinalIgnoreCase));
    }

    private static HttpRequestMessage Post(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Cookie", $"{UserSessionCookies.CsrfCookieName}=tok-1");
        request.Headers.Add(UserCsrfFilter.HeaderName, "tok-1");
        return request;
    }

    private static string AddItemBody() =>
        $$"""{"productCode":"{{DocumentNo}}","variantCode":"{{Group}}","quantity":1}""";

    private static string CreateSessionBody(Guid orderId) =>
        $$"""{"orderId":"{{orderId}}","method":"card","psp":"2c2p"}""";
}
