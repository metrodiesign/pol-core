extern alias ApiHost;

using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ApiHost::Api.Merchants;
using BuildingBlocks.Application;
using Carts.Application;
using Checkouts.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Payments.Application.Ports.Psp;
using Payments.Domain.Psp;
using Cart = Carts.Domain.Cart;
using CheckoutSession = Checkouts.Domain.Session;

namespace Hosts.Tests;

// products-external-source-of-truth task 5 Verify: line, as close to the literal HTTP round-trip as this
// environment allows. The manual recipe is "bootstrap fresh -> a merchant user LOGS IN -> GET /products is
// non-empty -> a checkout does not 409". There is no Google OIDC credential in dev/CI, so the ONE thing faked
// here is authentication (the merchant-user policy is re-pointed at an always-present scheme and the actor is
// bound to a SEEDED merchant + its SEEDED SaleCode, which is all a real login would have produced). Everything
// downstream of auth runs through the REAL host: the real route + policy + CSRF filter + minimal-API lambda +
// mediator pipeline, and — the point of this test versus MerchantLifecycleEndpointTests — the REAL
// SpDocumentGateway (against the live hippodb/mammothdb sim) and the REAL DocumentSaleProbe (against the live
// VCentralPay), both reading the rows docker/bootstrap/seed-demo.sql actually wrote. Cart/checkout persistence
// stays in-memory (the buy-path orchestration is proven in MerchantLifecycleEndpointTests); what this proves is
// the two SOURCE-facing reads the seed's realness depends on, exercised through the endpoints themselves.
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

/// <summary>Captures the host's Warning/Error log entries (with the exception) so a 500 can be attributed to a
/// concrete server-side exception in the test failure message — the response body of an opaque 500 carries none
/// of that, and on CI the run log keeps nothing but the status code.</summary>
file sealed class CapturingLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose() { }

    private sealed class CapturingLogger(string category, System.Collections.Concurrent.ConcurrentQueue<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var line = $"[{logLevel}] {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += $"\n    -> {exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}";
            sink.Enqueue(line);
        }
    }
}

file sealed class LiveCatalogueFactory : WebApplicationFactory<ApiHost::Program>
{
    public List<Cart> Carts { get; } = [];
    public List<CheckoutSession> Sessions { get; } = [];
    public System.Collections.Concurrent.ConcurrentQueue<string> ServerLogs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);   // Development skips the injected-credential/OIDC boot guards
        builder.UseSetting("ConnectionStrings:Migrator", "");   // DB is migrated + seeded out of band; no auto-migrate
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(ServerLogs)));
        // REAL VCentralPay: the DocumentSaleProbe's MerchantRuntimeDbContext reads shop.Orders/txn.PaymentSessions
        // here. This MUST go through UseSetting, not ConfigureAppConfiguration: Program.cs reads
        // GetConnectionString("App") at build time (Program.cs:137), and a factory ConfigureAppConfiguration
        // in-memory source is applied too late to be seen by that read — the host would fall back to
        // appsettings.Development.json's pol_app password, which on CI is NOT the ephemeral POL_APP_PASSWORD the
        // job generated, giving "Login failed for user 'pol_app'" (error 18456).
        builder.UseSetting("ConnectionStrings:App", IntegrationEnv.AppConn);
        builder.UseSetting("ConnectionStrings:Admin", IntegrationEnv.AppConn);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            // REAL upstream sim: the SpDocumentGateway singleton routes Motor -> hippodb, Non-Motor -> mammothdb.
            ["SpDocument:BranchCode"] = "000",
            ["SpDocument:MotorConnectionString"] = IntegrationEnv.Catalog("hippodb"),
            ["SpDocument:NonMotorConnectionString"] = IntegrationEnv.Catalog("mammothdb"),
        }));
        builder.ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestMerchantUserAuthHandler>(
                    TestMerchantUserAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("merchant-user", p => p
                .AddAuthenticationSchemes(TestMerchantUserAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            // Bound to a SEEDED merchant + its SEEDED SaleCode — what a real merchant OIDC login would have set.
            services.AddScoped<IActorContext>(_ => new BoundActor(
                MerchantCatalogueLiveEndpointTests.Merchant, MerchantCatalogueLiveEndpointTests.SaleCode));
            // Cart/checkout persistence is in-memory (orchestration is proven elsewhere); the PSP connection is
            // faked to enable the card channel so the checkout reaches its document reads.
            services.AddScoped<ICartRepository>(_ => new FakeCarts(Carts));
            services.AddScoped<ICheckoutRepository>(_ => new FakeCheckouts(Sessions));
            services.AddScoped<IUnitOfWork>(_ => new NoOpUnitOfWork());
            services.AddScoped<IConnectionRepository>(_ => new FakeConnections(
                MerchantCatalogueLiveEndpointTests.Merchant, "card,promptpay,installment"));

            // ISpDocumentGateway and IDocumentSaleProbe are DELIBERATELY not overridden here: the host's own
            // real registrations stand, so both reads hit the live sim / VCentralPay.
        });
    }
}

/// <summary>Live-suite connection strings, built from the same env vars as Integration.Tests' IntegrationDb
/// (POL_SQL_SERVER/POL_DB/POL_APP_PASSWORD/POL_SA_PASSWORD + the per-sim server/password), so this test runs
/// under the same CI wiring. Duplicated rather than referenced because IntegrationDb is internal to that
/// project.</summary>
file static class IntegrationEnv
{
    private static string Server => Get("POL_SQL_SERVER") ?? "localhost,11433";
    private static string Db => Get("POL_DB") ?? "VCentralPay";

    public static string AppConn => Conn(Server, Db, "pol_app", Require("POL_APP_PASSWORD"));
    public static string SaConn => Conn(Server, Db, "sa", Require("POL_SA_PASSWORD"));

    public static string Catalog(string catalog) => catalog switch
    {
        "hippodb" => Conn(Get("POL_HIPPO_SQL_SERVER") ?? "localhost,11434", catalog, "hippo_app", Require("POL_HIPPO_APP_PASSWORD")),
        "mammothdb" => Conn(Get("POL_MAMMOTH_SQL_SERVER") ?? "localhost,11435", catalog, "mammoth_app", Require("POL_MAMMOTH_APP_PASSWORD")),
        _ => throw new ArgumentOutOfRangeException(nameof(catalog), catalog, "Unknown simulated catalogue."),
    };

    // Connection pooling stays at the production default (on). IntegrationDb turns it off (Pooling=False) because
    // each of its tests opens one short-lived connection; this test boots a WHOLE host that opens several, so the
    // production default is the faithful choice here.
    private static string Conn(string server, string db, string user, string pw) =>
        $"Server={server};Database={db};User Id={user};Password={pw};Encrypt=True;TrustServerCertificate=True";

    private static string? Get(string key) => Environment.GetEnvironmentVariable(key);

    private static string Require(string key) => Get(key)
        ?? throw new InvalidOperationException($"Integration test needs env var '{key}' (see IntegrationDb / CI dotnet-integration).");
}

[Trait("Category", "Integration")]
public sealed class MerchantCatalogueLiveEndpointTests
{
    // Seeded merchant vprivilege (e1…0001) + its user's SaleCode 77001 (docker/bootstrap/seed-demo.sql):
    // vprivilege's 2C2P connection enables the card channel, and 77001 owns UNPAID rows on both sim sides.
    internal static readonly Guid Merchant = Guid.Parse("e1000000-0000-4000-8000-000000000001");
    internal const string SaleCode = "77001";
    private static readonly DateTime Now = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

    // Seeded cart-line documents that no seeded ORDER references, so the live probe reports them sellable
    // regardless of session timing (changes-t5.md: order items use a disjoint document set). Both are real,
    // in-window, UNPAID sim rows under SaleCode 77001 — one Motor (hippodb), one Non-Motor (mammothdb) —
    // so the pair also proves both gateway routes resolve over HTTP.
    private static readonly (string DocumentNo, string ProductGroup)[] SafeSeededDocuments =
    [
        ("69301/กธ/9100002", "CMI"),    // Motor -> hippodb
        ("26301/POL/000001", "FIRE"),   // Non-Motor -> mammothdb
    ];

    [Fact]
    public async Task Seeded_demo_lists_products_and_checks_out_over_http_against_the_live_upstream()
    {
        await RunSeedAsync();

        using var factory = new LiveCatalogueFactory();
        using var client = factory.CreateClient();

        // REQ-6.10 — GET /products over HTTP returns a non-empty catalogue for the merchant user's own bound
        // SaleCode, read live from the upstream. The old PRD-* placeholders would have paged empty here.
        var list = await client.GetAsync(
            "/api/v1/products?productFilters=" + Uri.EscapeDataString("""{"insuranceType":"Motor"}"""));
        await AssertOk(list, "GET /products", factory.ServerLogs);
        using var page = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.NotEmpty(page.RootElement.GetProperty("items").EnumerateArray());

        // REQ-6.9 — a seeded cart document added and checked out over HTTP does NOT 409: the live gateway
        // resolves it (in-window, non-PAID) and the live probe reports it sellable. This is the failure the
        // seed's DocumentNo realness exists to prevent, exercised through the real add-item + checkout endpoints.
        foreach (var (documentNo, productGroup) in SafeSeededDocuments)
        {
            factory.Carts.Clear();
            factory.Sessions.Clear();
            var cart = new Cart(Guid.CreateVersion7(), Merchant, Now);
            factory.Carts.Add(cart);

            var add = await client.SendAsync(Post($"/api/v1/carts/{cart.Id}/items", AddItemBody(documentNo, productGroup)));
            await AssertOk(add, $"POST /carts/items ({documentNo})", factory.ServerLogs);

            var checkout = await client.SendAsync(Post("/api/v1/checkouts", StartCheckoutBody(cart.Id, documentNo)));
            await AssertOk(checkout, $"POST /checkouts ({documentNo})", factory.ServerLogs);
        }
    }

    /// <summary>Asserts 200 and, when it is not, fails with the response body AND the host's captured Warning/Error
    /// log (which carries the actual server exception type + stack). The ProblemDetails body of a 500 is opaque by
    /// design, and on CI the run log keeps nothing but the status code — so without this the next red is a guess.</summary>
    private static async Task AssertOk(
        HttpResponseMessage response, string what, IEnumerable<string> serverLogs)
    {
        if (response.StatusCode == HttpStatusCode.OK)
            return;
        var body = await response.Content.ReadAsStringAsync();
        var logs = string.Join("\n", serverLogs);
        Assert.Fail(
            $"{what} expected 200 OK but got {(int)response.StatusCode} {response.StatusCode}.\nResponse body:\n{body}\n\nServer logs:\n{logs}");
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

    private static string AddItemBody(string documentNo, string productGroup) =>
        $$"""{"documentNo":"{{documentNo}}","productGroup":"{{productGroup}}","quantity":1}""";

    private static string StartCheckoutBody(Guid cartId, string documentNo) =>
        $$"""
        {"cartId":"{{cartId}}","paymentChannel":"CARD",
         "customer":{"name":"Somchai Jaidee","phone":"0812345678","email":"buyer@example.com"},
         "insuredPersons":[
          {"documentNo":"{{documentNo}}","firstName":"Somchai","lastName":"Jaidee",
           "idNumber":"1234567890123","dateOfBirth":"1990-01-01T00:00:00Z"}]}
        """;

    /// <summary>Runs docker/bootstrap/seed-demo.sql the way scripts/seed-demo.sh and the bootstrap container do:
    /// substitute <c>$(DbName)</c>, split on <c>GO</c>, execute one batch at a time on an sa connection. The seed
    /// is idempotent (delete-by-prefix then re-insert) and scoped to its own demo Id prefixes.</summary>
    private static async Task RunSeedAsync()
    {
        var db = Environment.GetEnvironmentVariable("POL_DB") ?? "VCentralPay";
        var script = (await File.ReadAllTextAsync(SeedScriptPath())).Replace("$(DbName)", db, StringComparison.Ordinal);

        await using var c = new SqlConnection(IntegrationEnv.SaConn);
        await c.OpenAsync();
        foreach (var batch in SplitBatches(script))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static IEnumerable<string> SplitBatches(string script)
    {
        var current = new List<string>();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                var batch = string.Join('\n', current).Trim();
                if (batch.Length > 0) yield return batch;
                current.Clear();
            }
            else
            {
                current.Add(line);
            }
        }
        var tail = string.Join('\n', current).Trim();
        if (tail.Length > 0) yield return tail;
    }

    private static string SeedScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docker", "bootstrap", "seed-demo.sql");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate docker/bootstrap/seed-demo.sql above the test output directory.");
    }
}
