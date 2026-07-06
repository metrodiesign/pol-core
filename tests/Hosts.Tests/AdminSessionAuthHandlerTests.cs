extern alias ApiHost;
using System.Text.Encodings.Web;
using ApiHost::Api;
using Admin.Application;
using Admin.Application.ResolveAdmin;
using Admin.Domain;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

/// <summary>
/// The AdminSession cookie authentication handler (REQ-4/5/9): decision table + principal (admin_tier/sub) +
/// IAdminScope binding + transparent rotation + idle-slide + reuse-driven family revocation, all exercised with
/// fakes (no DB, no mediator). The opaque token never appears in the session store — only its SHA-256 hash.
/// </summary>
public sealed class AdminSessionAuthHandlerTests
{
    private static readonly DateTime T0 = new(2026, 6, 24, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AdminId = Guid.Parse("a1111111-1111-1111-1111-111111111111");
    private static readonly AdminSessionPolicy Policy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60));

    private static AdminByIdResult Resolved =>
        AdminByIdResult.Of(new AdminResolution(AdminId, "ops@org.com", AdminTier.Super, AccessibleTenants.All), "google-sub-1");

    [Fact]
    public async Task No_cookie_yields_no_result_so_authorization_returns_401_without_consulting_bearer()
    {
        var (handler, _, _, scope, http) = await Make(T0, Resolved);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.None);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task Unknown_token_fails()
    {
        var (handler, store, _, _, http) = await Make(T0, Resolved);
        store.Seeded = null; // nothing matches the presented hash
        SetCookie(http, AdminSessionTokens.NewOpaqueToken());

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Live_active_session_authenticates_with_tier_and_sub_claims_and_binds_scope()
    {
        var token = AdminSessionTokens.NewOpaqueToken();
        var session = AdminSession.Start(AdminId, AdminSessionTokens.Hash(token), T0, Policy);
        var (handler, store, _, scope, http) = await Make(T0.AddSeconds(30), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("Super", result.Principal!.FindFirst("admin_tier")!.Value);
        Assert.Equal("google-sub-1", result.Principal.FindFirst("sub")!.Value);
        Assert.True(scope.IsBound);
        Assert.Equal(AdminId, scope.Current.AdminId);
        Assert.Empty(store.Added);          // young -> no rotation
        Assert.Null(store.Slid);            // within the 1-minute slide throttle
    }

    [Fact]
    public async Task Active_session_past_the_rotation_age_rotates_sets_a_new_cookie_and_audits()
    {
        var token = AdminSessionTokens.NewOpaqueToken();
        var session = AdminSession.Start(AdminId, AdminSessionTokens.Hash(token), T0, Policy);
        var (handler, store, audit, _, http) = await Make(T0.AddMinutes(16), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var successor = Assert.Single(store.Added);
        Assert.Equal(session.FamilyId, successor.FamilyId);          // same family
        Assert.Equal((session.Id, successor.Id), store.Superseded);  // atomic single-winner supersede
        Assert.Contains(http.Response.Headers.SetCookie, c => c!.Contains("adm_session", StringComparison.Ordinal));
        Assert.Contains(audit.Appended, a => a.EventType == AdminAuthEventType.Rotated && a.AdminAccountId == AdminId);
    }

    [Fact]
    public async Task Active_session_slides_idle_lazily_when_past_the_throttle_without_rotating()
    {
        var token = AdminSessionTokens.NewOpaqueToken();
        var session = AdminSession.Start(AdminId, AdminSessionTokens.Hash(token), T0, Policy);
        var (handler, store, _, _, http) = await Make(T0.AddMinutes(2), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(store.Added);                                   // not yet at rotation age
        Assert.NotNull(store.Slid);
        Assert.Equal(session.Id, store.Slid!.Value.id);
        Assert.Equal(T0.AddMinutes(2) + Policy.Idle, store.Slid.Value.idle); // bounded by absolute (not hit here)
    }

    [Fact]
    public async Task Expired_active_session_is_rejected()
    {
        var token = AdminSessionTokens.NewOpaqueToken();
        var session = AdminSession.Start(AdminId, AdminSessionTokens.Hash(token), T0, Policy);
        var (handler, _, _, scope, http) = await Make(T0.AddMinutes(31), Resolved, token, session); // past idle (30m)

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task Immediate_predecessor_within_grace_is_served_without_rotating()
    {
        var token = AdminSessionTokens.NewOpaqueToken();
        var predecessor = AdminSession.Start(AdminId, AdminSessionTokens.Hash(token), T0, Policy);
        var successor = predecessor.Rotate(AdminSessionTokens.Hash(AdminSessionTokens.NewOpaqueToken()), T0.AddMinutes(15), Policy);
        // present the (now Superseded) predecessor 30s after rotation — a legit in-flight lag (grace 60s).
        var (handler, store, _, scope, http) = await Make(T0.AddMinutes(15).AddSeconds(30), Resolved, token, predecessor);
        store.FamilyActiveId = successor.Id;

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.True(scope.IsBound);
        Assert.Empty(store.Added);          // a grace predecessor never rotates again
        Assert.Null(store.Superseded);
    }

    [Fact]
    public async Task Superseded_token_past_grace_is_treated_as_reuse_and_revokes_the_family()
    {
        var token = AdminSessionTokens.NewOpaqueToken();
        var predecessor = AdminSession.Start(AdminId, AdminSessionTokens.Hash(token), T0, Policy);
        var successor = predecessor.Rotate(AdminSessionTokens.Hash(AdminSessionTokens.NewOpaqueToken()), T0.AddMinutes(15), Policy);
        var (handler, store, audit, scope, http) = await Make(T0.AddMinutes(17), Resolved, token, predecessor); // 2m > 60s grace
        store.FamilyActiveId = successor.Id;

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
        Assert.Equal(predecessor.FamilyId, store.RevokedFamily);
        Assert.Contains(audit.Appended, a => a.EventType == AdminAuthEventType.FamilyRevokedReuse && a.AdminAccountId == AdminId);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task A_suspended_admin_is_rejected_even_with_a_live_session()
    {
        var token = AdminSessionTokens.NewOpaqueToken();
        var session = AdminSession.Start(AdminId, AdminSessionTokens.Hash(token), T0, Policy);
        var (handler, _, _, scope, http) = await Make(T0.AddSeconds(30), AdminByIdResult.Suspended, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);  // REQ-9.2: suspension takes effect without re-login
        Assert.False(scope.IsBound);
    }

    // --- harness ---

    private static async Task<(AdminSessionAuthenticationHandler handler, FakeStore store, FakeAudit audit, AdminScope scope, DefaultHttpContext http)>
        Make(DateTime now, AdminByIdResult resolverResult, string? cookieToken = null, AdminSession? seeded = null)
    {
        var store = new FakeStore { Seeded = seeded };
        var audit = new FakeAudit();
        var scope = new AdminScope();
        var cookies = new AdminSessionCookies(Options.Create(new AdminSessionOptions()), new Env());
        var handler = new AdminSessionAuthenticationHandler(
            new StubMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default,
            store, audit, cookies, new FakeResolver(resolverResult), scope, new TestClock(now),
            Options.Create(new AdminSessionOptions()));

        var http = new DefaultHttpContext();
        if (cookieToken is not null)
            SetCookie(http, cookieToken);

        await handler.InitializeAsync(
            new AuthenticationScheme(AdminSessionAuthenticationHandler.SchemeName, null, typeof(AdminSessionAuthenticationHandler)),
            http);
        return (handler, store, audit, scope, http);
    }

    private static void SetCookie(HttpContext http, string token) =>
        http.Request.Headers.Cookie = $"{AdminSessionCookies.SessionCookieName}={token}";

    private sealed class FakeStore : IAdminSessionStore
    {
        public AdminSession? Seeded;
        public Guid? FamilyActiveId;
        public bool SupersedeWins = true;
        public readonly List<AdminSession> Added = [];
        public (Guid id, Guid succ)? Superseded;
        public Guid? RevokedFamily;
        public Guid? RevokedAdmin;
        public (Guid id, DateTime idle)? Slid;
        public int SaveCount;

        public Task<AdminSession?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) =>
            Task.FromResult(Seeded is not null && Seeded.TokenHash.AsSpan().SequenceEqual(hash) ? Seeded : null);
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult(FamilyActiveId);
        public void Add(AdminSession session) => Added.Add(session);
        public Task<int> SaveChangesAsync(CancellationToken ct) { SaveCount++; return Task.FromResult(1); }
        public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) { Superseded = (id, succ); return Task.FromResult(SupersedeWins); }
        public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) { Slid = (id, idle); return Task.CompletedTask; }
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) { RevokedFamily = familyId; return Task.CompletedTask; }
        public Task RevokeAllForAdminAsync(Guid adminId, CancellationToken ct) { RevokedAdmin = adminId; return Task.CompletedTask; }
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<AdminSession>> ListByAdminAsync(Guid adminAccountId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AdminSession>>([]);
        public Task<AdminSession?> FindByIdAsync(Guid sessionId, CancellationToken ct) => Task.FromResult<AdminSession?>(null);
    }

    private sealed class FakeAudit : IAdminAuthAuditWriter
    {
        public readonly List<AdminAuthAudit> Appended = [];
        public void Append(AdminAuthAudit entry) => Appended.Add(entry);
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
    }

    private sealed class FakeResolver(AdminByIdResult result) : IAdminSessionResolver
    {
        public Task<AdminByIdResult> ResolveByIdAsync(Guid adminAccountId, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class StubMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production; // not dev-http -> __Host- cookie name
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
