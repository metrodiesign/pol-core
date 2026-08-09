extern alias ApiHost;
using System.Text.Encodings.Web;
using ApiHost::Api;
using ApiHost::Api.Merchants;
using BuildingBlocks.Application;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
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
    private static readonly SessionPolicy Policy =
        new(TimeSpan.FromHours(24), TimeSpan.FromDays(7), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(60));

    private static ByIdResult Resolved => ResolvedWith(null);

    private static ByIdResult ResolvedWith(string? saleCode) =>
        ByIdResult.Of(
            new Resolution(UserId, "p@org.com", MerchantId, new HashSet<string> { "payment.create" }, saleCode),
            "google-sub-1");

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
        SetCookie(http, UserTokens.NewOpaqueToken());

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Live_active_session_authenticates_with_merchant_id_and_sub_claims_and_binds_scope()
    {
        var token = UserTokens.NewOpaqueToken();
        var session = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var (handler, store, _, scope, _) = await Make(T0.AddSeconds(30), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(MerchantId.ToString(), result.Principal!.FindFirst("merchant_id")!.Value); // HttpActorContext path (S4)
        Assert.Equal("google-sub-1", result.Principal.FindFirst("sub")!.Value);
        Assert.Null(result.Principal.FindFirst("role")); // no role claim — single-scheme, no Bearer principal to distinguish from (T11)
        Assert.True(scope.IsBound);
        Assert.Equal(UserId, scope.Current.UserId);
        Assert.Empty(store.Added);
        Assert.Null(store.Slid);
    }

    // products-external-source-of-truth REQ-4.8: the catalogue searches under the account's OWN sale code, so
    // the claim is minted here from the freshly resolved account on every request — never taken from the client,
    // and never stale (revoking the code takes effect on the next request, like every other resolved field).
    [Fact]
    public async Task Live_session_carries_the_resolved_accounts_sale_code_as_a_claim()
    {
        var token = UserTokens.NewOpaqueToken();
        var session = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var (handler, _, _, _, _) = await Make(T0.AddSeconds(30), ResolvedWith("77001"), token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("77001", result.Principal!.FindFirst("sale_code")!.Value);
    }

    // An account with no sale code bound gets NO claim rather than an empty one — the catalogue path must be
    // able to tell "has none" apart from "has a blank one" to answer 403 (REQ-4.9).
    [Fact]
    public async Task An_account_without_a_sale_code_gets_no_sale_code_claim()
    {
        var token = UserTokens.NewOpaqueToken();
        var session = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var (handler, _, _, _, _) = await Make(T0.AddSeconds(30), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.Principal!.FindFirst("sale_code"));
    }

    [Fact]
    public async Task Active_session_past_the_rotation_age_rotates_sets_a_new_cookie_and_audits()
    {
        var token = UserTokens.NewOpaqueToken();
        var session = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var (handler, store, audit, _, http) = await Make(T0.AddMinutes(16), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var successor = Assert.Single(store.Added);
        Assert.Equal(session.FamilyId, successor.FamilyId);
        Assert.Equal((session.Id, successor.Id), store.Superseded);
        Assert.Contains(http.Response.Headers.SetCookie, c => c!.Contains("mch_session", StringComparison.Ordinal));
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.Rotated && a.UserId == UserId);
    }

    [Fact]
    public async Task Active_session_slides_idle_lazily_when_past_the_throttle_without_rotating()
    {
        var token = UserTokens.NewOpaqueToken();
        var session = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
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
        var token = UserTokens.NewOpaqueToken();
        var session = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var (handler, _, _, scope, _) = await Make(T0.AddHours(24).AddMinutes(1), Resolved, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task Immediate_predecessor_within_grace_is_served_without_rotating()
    {
        var token = UserTokens.NewOpaqueToken();
        var predecessor = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var successor = predecessor.Rotate(UserTokens.Hash(UserTokens.NewOpaqueToken()), T0.AddMinutes(15), Policy);
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
        var token = UserTokens.NewOpaqueToken();
        var predecessor = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var successor = predecessor.Rotate(UserTokens.Hash(UserTokens.NewOpaqueToken()), T0.AddMinutes(15), Policy);
        var (handler, store, audit, scope, _) = await Make(T0.AddMinutes(17), Resolved, token, predecessor);
        store.FamilyActiveId = successor.Id;

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure);
        Assert.Equal(predecessor.FamilyId, store.RevokedFamily);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.FamilyRevokedReuse && a.UserId == UserId);
        Assert.False(scope.IsBound);
    }

    [Fact]
    public async Task A_suspended_merchant_user_is_rejected_even_with_a_live_session()
    {
        var token = UserTokens.NewOpaqueToken();
        var session = Session.Start(UserId, UserTokens.Hash(token), T0, Policy);
        var (handler, _, _, scope, _) = await Make(T0.AddSeconds(30), ByIdResult.NotActive, token, session);

        var result = await handler.AuthenticateAsync();

        Assert.NotNull(result.Failure); // REQ-12.4: suspension takes effect within one request
        Assert.False(scope.IsBound);
    }

    // --- harness ---

    private static async Task<(UserSessionAuthenticationHandler handler, FakeStore store, FakeAudit audit, UserScope scope, DefaultHttpContext http)>
        Make(DateTime now, ByIdResult resolverResult, string? cookieToken = null, Session? seeded = null)
    {
        var store = new FakeStore { Seeded = seeded };
        var audit = new FakeAudit();
        var scope = new UserScope();
        var cookies = new UserSessionCookies(Options.Create(new UserSessionOptions()), new Env());
        var handler = new UserSessionAuthenticationHandler(
            new StubMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default,
            store, audit, cookies, new FakeResolver(resolverResult), scope, new TestClock(now),
            Options.Create(new UserSessionOptions()));

        var http = new DefaultHttpContext();
        if (cookieToken is not null)
            SetCookie(http, cookieToken);

        await handler.InitializeAsync(
            new AuthenticationScheme(UserSessionAuthenticationHandler.SchemeName, null, typeof(UserSessionAuthenticationHandler)),
            http);
        return (handler, store, audit, scope, http);
    }

    private static void SetCookie(HttpContext http, string token) =>
        http.Request.Headers.Cookie = $"{UserSessionCookies.SessionCookieName}={token}";

    private sealed class FakeStore : ISessionStore
    {
        public Session? Seeded;
        public Guid? FamilyActiveId;
        public bool SupersedeWins = true;
        public readonly List<Session> Added = [];
        public (Guid id, Guid succ)? Superseded;
        public Guid? RevokedFamily;
        public Guid? RevokedUser;
        public (Guid id, DateTime idle)? Slid;

        public Task<Session?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) =>
            Task.FromResult(Seeded is not null && Seeded.TokenHash.AsSpan().SequenceEqual(hash) ? Seeded : null);
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult(FamilyActiveId);
        public void Add(Session session) => Added.Add(session);
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) { Superseded = (id, succ); return Task.FromResult(SupersedeWins); }
        public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) { Slid = (id, idle); return Task.CompletedTask; }
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) { RevokedFamily = familyId; return Task.CompletedTask; }
        public Task RevokeAllForUserAsync(Guid merchantUserId, CancellationToken ct) { RevokedUser = merchantUserId; return Task.CompletedTask; }
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeAudit : IAuthAuditWriter
    {
        public readonly List<AuthAudit> Appended = [];
        public void Append(AuthAudit entry) => Appended.Add(entry);
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
    }

    private sealed class FakeResolver(ByIdResult result) : IUserSessionResolver
    {
        public Task<ByIdResult> ResolveByIdAsync(Guid merchantUserId, CancellationToken ct) => Task.FromResult(result);
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
