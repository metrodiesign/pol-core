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

file sealed class BoundAdminScope(IReadOnlySet<string> permissions) : IAdminScope
{
    public bool IsBound => true;
    public Resolution Current { get; } =
        new(Guid.NewGuid(), "admin@org.com", Tier.Super, AccessibleMerchants.All) { Permissions = permissions };
    public AccessibleMerchants Accessible => AccessibleMerchants.All;
}

file sealed class FakeResolver : IAccountResolver
{
    public static readonly Guid UserId = Guid.NewGuid();
    public Task<AccountSnapshot?> FindBySubjectAsync(string subject, CancellationToken ct) =>
        Task.FromResult<AccountSnapshot?>(subject == "g-sub-1"
            ? new AccountSnapshot(UserId, subject, "somchai@example.com", null, MerchantUserStatus.PendingApproval)
            : null);
    public Task<AccountSnapshot?> FindByIdAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<AccountSnapshot?>(null);
}

file sealed class FakeHistoryReader : IRegistrationHistoryReader
{
    public Task<IReadOnlyList<RegistrationAttempt>> ListAttemptsAsync(Guid merchantUserId, CancellationToken ct)
    {
        var user = MerchantUser.Register("g-sub-1", "somchai@example.com", DateTime.UtcNow);
        user.SetDetails("Somchai", "Jaidee", PersonType.Individual, "1234567890123", "PC-1", "LN-99999", "0812345678");
        return Task.FromResult<IReadOnlyList<RegistrationAttempt>>(
            [RegistrationAttempt.Capture(user, 1, TicketPurpose.Registration, "somchai@example.com", DateTime.UtcNow)]);
    }

    public Task<IReadOnlyList<RegistrationAudit>> ListAuditsAsync(string targetSubject, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RegistrationAudit>>(
            [RegistrationAudit.For(RegistrationAuditAction.Registered, targetSubject, "corr-a", DateTime.UtcNow)]);
}

file sealed class FakeAuditWriter : IRegistrationAuditWriter
{
    public static readonly List<RegistrationAudit> Appended = [];
    public void Append(RegistrationAudit audit) => Appended.Add(audit);
}

file sealed class NoOpUserUow : IUserUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        operation(ct);
}

file sealed class HistoryFactory(bool grantViewKey) : WebApplicationFactory<ApiHost::Program>
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
                .AddScheme<AuthenticationSchemeOptions, TestAdminAuthHandler>(TestAdminAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthorizationOptions>(o => o.AddPolicy("admin", p => p
                .AddAuthenticationSchemes(TestAdminAuthHandler.SchemeName)
                .RequireAuthenticatedUser()));

            IReadOnlySet<string> permissions = grantViewKey
                ? new HashSet<string> { Keys.MerchantUserView }
                : new HashSet<string>();
            services.AddScoped<IAdminScope>(_ => new BoundAdminScope(permissions));

            // Last-registered wins for these Scoped ports — the handler runs for real, no DB behind it.
            services.AddScoped<IAccountResolver, FakeResolver>();
            services.AddScoped<IRegistrationHistoryReader, FakeHistoryReader>();
            services.AddScoped<IRegistrationAuditWriter, FakeAuditWriter>();
            services.AddScoped<IUserUnitOfWork, NoOpUserUow>();
        });
    }
}

public sealed class RegistrationHistoryEndpointTests
{
    private const string Route = "/api/v1/admins/merchants/users/g-sub-1/registrations";

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
        Assert.Equal("****0123", attempt.GetProperty("idNumber").GetString());       // masked default (REQ-3.1)
        Assert.Equal("****5678", attempt.GetProperty("phone").GetString());
        Assert.Equal("s***@example.com", attempt.GetProperty("email").GetString());  // REQ-3.2
        Assert.Equal("Somchai", attempt.GetProperty("firstName").GetString());       // names full (REQ-3.3)
        Assert.Equal("Registration", attempt.GetProperty("purpose").GetString());    // enum as string on the wire
        Assert.Equal("registered",
            body.RootElement.GetProperty("timeline")[0].GetProperty("action").GetString()); // REQ-2.3
    }

    [Fact]
    public async Task Unknown_subject_returns_404()
    {
        using var factory = new HistoryFactory(grantViewKey: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/admins/merchants/users/missing/registrations");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // REQ-2.5
    }
}
