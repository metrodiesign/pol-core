extern alias ApiHost;
using System.Text.Encodings.Web;
using ApiHost::Api;
using BuildingBlocks.Application;
using Merchants.Application;
using Merchants.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

/// <summary>
/// The MerchantUserSession cookie authentication handler (REQ-11/12/17): decision table + principal
/// (merchant_id/sub) + IMerchantUserScope binding + transparent rotation + idle-slide + reuse-driven family
/// revocation, all exercised with fakes (no DB, no mediator). No cookie -&gt; NoResult (T11: single-scheme, no
/// Bearer fallback left to try — the merchant-user policy denies 401 directly); a non-Active merchant user is
/// rejected even with a live session (suspend takes effect within one request, REQ-12.4).
/// </summary>
public sealed class MerchantUserSessionAuthHandlerTests
{
    private static readonly DateTime T0 = new(2026, 6, 28, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    private static readonly Guid MerchantId = Guid.Parse("d2222222-2222-2222-2222-222222222222");
    private static readonly MerchantUserSessionPolicy Policy =
        new(TimeSpan.FromMinutes(30), TimeSpan.FromHours(8), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60));

    private static MerchantUserByIdResult Resolved =>
        MerchantUserByIdResult.Of(new MerchantUserResolution(UserId, "p@org.com", MerchantId, new HashSet<string> { "product.create" }), "google-sub-1");

    [Fact]
    public async Task No_cookie_yields_no_result_so_authorization_returns_401_directly()
    {
        var (handler, _, _, scope, _) = await Make(T0, Resolved);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.None);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task Unknown_token_fails()
    {
        var (handler, store, _, _, http) = await Make(T0, Resolved);
        store.Seeded = null;
        SetCookie(http, MerchantUserTokens.NewOpaqueToken());

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Live_active_session_authenticates_with_merchant_id_and_sub_claims_and_binds_scope()
    {
        var token = MerchantUserTokens.NewOpaqueToken();
        var session = MerchantUserSession.Start(UserId, MerchantUserTokens.Hash(token), T0, Policy);
        var (handler, store, _, scope, _) = await Make(T0.AddSeconds(30), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(MerchantId.ToString(), result.Principal!.FindFirst("merchant_id")!.Value); // HttpActorContext path (S4)
        Assert.Equal("google-sub-1", result.Principal.FindFirst("sub")!.Value);
        Assert.Null(result.Principal.FindFirst("role")); // no role claim — single-scheme, no Bearer principal to distinguish from (T11)
        Assert.True(scope.IsBound);
        Assert.Equal(UserId, scope.Current.MerchantUserId);
        Assert.Empty(store.Added);
        Assert.Null(store.Slid);
    }

    [Fact]
    public async Task Active_session_past_the_rotation_age_rotates_sets_a_new_cookie_and_audits()
    {
        var token = MerchantUserTokens.NewOpaqueToken();
        var session = MerchantUserSession.Start(UserId, MerchantUserTokens.Hash(token), T0, Policy);
        var (handler, store, audit, _, http) = await Make(T0.AddMinutes(16), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var successor = Assert.Single(store.Added);
        Assert.Equal(session.FamilyId, successor.FamilyId);
        Assert.Equal((session.Id, successor.Id), store.Superseded);
        Assert.Contains(http.Response.Headers.SetCookie, c => c!.Contains("mch_session", StringComparison.Ordinal));
        Assert.Contains(audit.Appended, a => a.EventType == MerchantAuthEventType.Rotated && a.MerchantUserId == UserId);
    }

    [Fact]
    public async Task Active_session_slides_idle_lazily_when_past_the_throttle_without_rotating()
    {
        var token = MerchantUserTokens.NewOpaqueToken();
        var session = MerchantUserSession.Start(UserId, MerchantUserTokens.Hash(token), T0, Policy);
        var (handler, store, _, _, _) = await Make(T0.AddMinutes(2), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(store.Added);
        Assert.NotNull(store.Slid);
        Assert.Equal(session.Id, store.Slid!.Value.id);
        Assert.Equal(T0.AddMinutes(2) + Policy.Idle, store.Slid.Value.idle);
    }

    [Fact]
    public async Task Expired_active_session_is_rejected()
    {
        var token = MerchantUserTokens.NewOpaqueToken();
        var session = MerchantUserSession.Start(UserId, MerchantUserTokens.Hash(token), T0, Policy);
        var (handler, _, _, scope, _) = await Make(T0.AddMinutes(31), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task Immediate_predecessor_within_grace_is_served_without_rotating()
    {
        var token = MerchantUserTokens.NewOpaqueToken();
        var predecessor = MerchantUserSession.Start(UserId, MerchantUserTokens.Hash(token), T0, Policy);
        var successor = predecessor.Rotate(MerchantUserTokens.Hash(MerchantUserTokens.NewOpaqueToken()), T0.AddMinutes(15), Policy);
        var (handler, store, _, scope, _) = await Make(T0.AddMinutes(15).AddSeconds(30), Resolved, token, predecessor);
        store.FamilyActiveId = successor.Id;

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.True(scope.IsBound);
        Assert.Empty(store.Added);
        Assert.Null(store.Superseded);
    }

    [Fact]
    public async Task Superseded_token_past_grace_is_treated_as_reuse_and_revokes_the_family()
    {
        var token = MerchantUserTokens.NewOpaqueToken();
        var predecessor = MerchantUserSession.Start(UserId, MerchantUserTokens.Hash(token), T0, Policy);
        var successor = predecessor.Rotate(MerchantUserTokens.Hash(MerchantUserTokens.NewOpaqueToken()), T0.AddMinutes(15), Policy);
        var (handler, store, audit, scope, _) = await Make(T0.AddMinutes(17), Resolved, token, predecessor);
        store.FamilyActiveId = successor.Id;

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
        Assert.Equal(predecessor.FamilyId, store.RevokedFamily);
        Assert.Contains(audit.Appended, a => a.EventType == MerchantAuthEventType.FamilyRevokedReuse && a.MerchantUserId == UserId);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task A_suspended_merchant_user_is_rejected_even_with_a_live_session()
    {
        var token = MerchantUserTokens.NewOpaqueToken();
        var session = MerchantUserSession.Start(UserId, MerchantUserTokens.Hash(token), T0, Policy);
        var (handler, _, _, scope, _) = await Make(T0.AddSeconds(30), MerchantUserByIdResult.NotActive, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure); // REQ-12.4: suspension takes effect within one request
        Assert.False(scope.IsBound);
    }

    // --- harness ---

    private static async Task<(MerchantUserSessionAuthenticationHandler handler, FakeStore store, FakeAudit audit, MerchantUserScope scope, DefaultHttpContext http)>
        Make(DateTime now, MerchantUserByIdResult resolverResult, string? cookieToken = null, MerchantUserSession? seeded = null)
    {
        var store = new FakeStore { Seeded = seeded };
        var audit = new FakeAudit();
        var scope = new MerchantUserScope();
        var cookies = new MerchantUserSessionCookies(Options.Create(new MerchantUserSessionOptions()), new Env());
        var handler = new MerchantUserSessionAuthenticationHandler(
            new StubMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default,
            store, audit, cookies, new FakeResolver(resolverResult), scope, new TestClock(now),
            Options.Create(new MerchantUserSessionOptions()));

        var http = new DefaultHttpContext();
        if (cookieToken is not null)
            SetCookie(http, cookieToken);

        await handler.InitializeAsync(
            new AuthenticationScheme(MerchantUserSessionAuthenticationHandler.SchemeName, null, typeof(MerchantUserSessionAuthenticationHandler)),
            http);
        return (handler, store, audit, scope, http);
    }

    private static void SetCookie(HttpContext http, string token) =>
        http.Request.Headers.Cookie = $"{MerchantUserSessionCookies.SessionCookieName}={token}";

    private sealed class FakeStore : IMerchantUserSessionStore
    {
        public MerchantUserSession? Seeded;
        public Guid? FamilyActiveId;
        public bool SupersedeWins = true;
        public readonly List<MerchantUserSession> Added = [];
        public (Guid id, Guid succ)? Superseded;
        public Guid? RevokedFamily;
        public Guid? RevokedUser;
        public (Guid id, DateTime idle)? Slid;

        public Task<MerchantUserSession?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) =>
            Task.FromResult(Seeded is not null && Seeded.TokenHash.AsSpan().SequenceEqual(hash) ? Seeded : null);
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult(FamilyActiveId);
        public void Add(MerchantUserSession session) => Added.Add(session);
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) { Superseded = (id, succ); return Task.FromResult(SupersedeWins); }
        public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) { Slid = (id, idle); return Task.CompletedTask; }
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) { RevokedFamily = familyId; return Task.CompletedTask; }
        public Task RevokeAllForUserAsync(Guid merchantUserId, CancellationToken ct) { RevokedUser = merchantUserId; return Task.CompletedTask; }
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeAudit : IMerchantAuthAuditWriter
    {
        public readonly List<MerchantAuthAudit> Appended = [];
        public void Append(MerchantAuthAudit entry) => Appended.Add(entry);
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
    }

    private sealed class FakeResolver(MerchantUserByIdResult result) : IMerchantUserSessionResolver
    {
        public Task<MerchantUserByIdResult> ResolveByIdAsync(Guid merchantUserId, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class TestClock(DateTime now) : IClock { public DateTime UtcNow { get; } = now; }

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
