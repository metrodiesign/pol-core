extern alias ApiHost;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Merchants.Application.Users;
using Merchants.Domain.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Resolution = Admins.Application.Users.Resolution;
using MerchantUser = Merchants.Domain.Users.User;
using MerchantUserStatus = Merchants.Domain.Users.UserStatus;
using SharedKernel;

namespace Hosts.Tests;

// registration-attempt-history REQ-2.1/3.1/4.2/4.3, driven through the REAL pipeline (route + "admin" policy
// + RequirePermission filter + Mediator + the real GetRegistrationHistoryHandler): no key -> 403, key -> 200,
// and — the B4 default — a request that OMITS ?reveal= binds reveal=false and returns MASKED values (not 400).
// The "admin" policy is re-pointed at a fake always-present scheme (same trick as
// AdminMerchantsEndpointControlsTests) and IAdminScope is replaced with a bound fake whose permission set the
// test controls; the Merchants.Application ports are faked so no live DB is touched.

file sealed class TestAdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestAdmin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("sub", "admin-sub-1")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

file sealed class BoundAdminScope(IReadOnlySet<string> permissions, AccessibleMerchants accessible) : IAdminScope
{
    public static readonly Guid AdminId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    public bool IsBound => true;
    public Resolution Current { get; } =
        new(AdminId, "admin@org.com", Tier.Super, accessible) { Permissions = permissions };
    public AccessibleMerchants Accessible => accessible;
}

file sealed class FakeResolver : IAccountResolver
{
    public static readonly Guid UserId = Guid.NewGuid();
    public static readonly Guid ActiveUserId = Guid.NewGuid();
    public static readonly Guid ActiveMerchantId = Guid.NewGuid();

    public Task<AccountSnapshot?> FindByIdentityAsync(ProviderIdentity identity, CancellationToken ct) =>
        Task.FromResult<AccountSnapshot?>(null);
    public Task<AccountSnapshot?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<AccountSnapshot?>(id switch
        {
            _ when id == UserId => new AccountSnapshot(UserId, "g-sub-1", "somchai@example.com", null,
                MerchantUserStatus.PendingApproval),
            _ when id == ActiveUserId => new AccountSnapshot(ActiveUserId, "g-sub-active", "somchai@example.com",
                ActiveMerchantId, MerchantUserStatus.Active),
            _ => null,
        });
}

file sealed class FakeHistoryReader : IRegistrationHistoryReader
{
    public Task<IReadOnlyList<RegistrationAttempt>> ListAttemptsAsync(Guid merchantUserId, CancellationToken ct)
    {
        var user = MerchantUser.Register("google", "g-sub-1", "somchai@example.com", DateTime.UtcNow);
        user.SetDetails("Somchai", "Jaidee", IdentityType.Individual, "1234567890123", "PC-1", "LN-99999", "0812345678");
        return Task.FromResult<IReadOnlyList<RegistrationAttempt>>(
            [RegistrationAttempt.Capture(user, 1, TicketPurpose.Registration, "somchai@example.com", DateTime.UtcNow)]);
    }

    public Task<IReadOnlyList<RegistrationAudit>> ListAuditsAsync(Guid targetUserId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RegistrationAudit>>(
            [RegistrationAudit.For(RegistrationAuditAction.Registered, targetUserId, "g-sub-1", "corr-a", DateTime.UtcNow)]);
}

file sealed class FakeAuditWriter : IRegistrationAuditWriter
{
    public readonly List<RegistrationAudit> Appended = [];
    public void Append(RegistrationAudit audit) => Appended.Add(audit);
}

file sealed class NoOpUserUow : IUserUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        operation(ct);
}

file sealed class HistoryFactory(bool grantViewKey, AccessibleMerchants? accessible = null)
    : WebApplicationFactory<ApiHost::Program>
{
    public FakeAuditWriter AuditWriter { get; } = new();

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
                .AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>(TestAdminAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("admin", p => p
                .AddAuthenticationSchemes(TestAdminAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            IReadOnlySet<string> permissions = grantViewKey
                ? new HashSet<string> { Keys.MerchantUserView }
                : new HashSet<string>();
            services.AddScoped<IAdminScope>(_ =>
                new BoundAdminScope(permissions, accessible ?? AccessibleMerchants.All));

            // Last-registered wins for these Scoped ports — the handler runs for real, no DB behind it.
            services.AddScoped<IAccountResolver, FakeResolver>();
            services.AddScoped<IRegistrationHistoryReader, FakeHistoryReader>();
            services.AddScoped<IRegistrationAuditWriter>(_ => AuditWriter);
            services.AddScoped<IUserUnitOfWork, NoOpUserUow>();
        });
    }
}

public sealed class RegistrationHistoryEndpointTests
{
    private static readonly string Route = $"/api/v1/admins/merchants/users/{FakeResolver.UserId}/registrations";

    [Fact]
    public async Task Without_the_view_key_the_gate_returns_403()
    {
        using var factory = new HistoryFactory(grantViewKey: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // REQ-4.2/4.3 fail-closed
    }

    [Fact]
    public async Task With_the_view_key_and_no_reveal_parameter_the_response_is_200_and_masked()
    {
        using var factory = new HistoryFactory(grantViewKey: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route); // no ?reveal= at all — must bind false, not 400 (B4)

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attempt = body.RootElement.GetProperty("attempts")[0];
        Assert.Equal("****0123", attempt.GetProperty("identityNumber").GetString()); // masked default (REQ-3.1)
        Assert.Equal("****5678", attempt.GetProperty("phone").GetString());
        Assert.Equal("s***@example.com", attempt.GetProperty("email").GetString());  // REQ-3.2
        Assert.Equal("Somchai", attempt.GetProperty("firstName").GetString());       // names full (REQ-3.3)
        Assert.Equal("Registration", attempt.GetProperty("purpose").GetString());    // enum as string on the wire
        Assert.Equal("registered",
            body.RootElement.GetProperty("timeline")[0].GetProperty("action").GetString()); // REQ-2.3
    }

    // products-external-source-of-truth REQ-10.3/10.5/10.7: the field this endpoint returns the merchant user's
    // sale code under is spelled saleCode, the retired producerCode spelling is absent, and — unchanged by the
    // rename — the value is NOT masked. The result record is serialised straight to the wire with no mapping
    // layer, so its member name IS the contract; renaming the member without a test here would silently rename
    // the JSON key for every consumer.
    [Fact]
    public async Task The_attempt_carries_saleCode_unmasked_and_no_producerCode()
    {
        using var factory = new HistoryFactory(grantViewKey: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(Route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var attempt = body.RootElement.GetProperty("attempts")[0];
        Assert.Equal("PC-1", attempt.GetProperty("saleCode").GetString());        // full value, never masked
        Assert.False(attempt.TryGetProperty("producerCode", out _));
    }

    [Fact]
    public async Task Reveal_audit_uses_internal_admin_actor_subject()
    {
        using var factory = new HistoryFactory(grantViewKey: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{Route}?reveal=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var audit = Assert.Single(factory.AuditWriter.Appended);
        Assert.Equal($"admin:{BoundAdminScope.AdminId:D}", audit.ActorSubject);
    }

    [Fact]
    public async Task Unknown_subject_returns_404()
    {
        using var factory = new HistoryFactory(grantViewKey: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/admins/merchants/users/{Guid.NewGuid()}/registrations");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // REQ-2.5
    }

    // REQ-2.7 (review PR #161): a Scoped admin holding the view key still cannot read a merchant-bound user
    // outside its accessible set — indistinguishable from not-found (no existence leak).
    [Fact]
    public async Task Scoped_admin_outside_the_targets_merchant_gets_404()
    {
        using var factory = new HistoryFactory(grantViewKey: true,
            accessible: AccessibleMerchants.Of(new HashSet<Guid> { Guid.NewGuid() }));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/admins/merchants/users/{FakeResolver.ActiveUserId}/registrations?reveal=true");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Scoped_admin_inside_the_targets_merchant_reads_the_history()
    {
        using var factory = new HistoryFactory(grantViewKey: true,
            accessible: AccessibleMerchants.Of(new HashSet<Guid> { FakeResolver.ActiveMerchantId }));
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/admins/merchants/users/{FakeResolver.ActiveUserId}/registrations");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
