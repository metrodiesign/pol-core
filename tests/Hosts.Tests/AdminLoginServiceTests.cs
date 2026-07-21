extern alias ApiHost;
using ApiHost::Api;
using ApiHost::Api.Admins;
using Admins.Application;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hosts.Tests;

/// <summary>
/// Callback-time session establishment / denial for the admin BFF (REQ-2.5/2.6/2.7/3.1/12.1/12.4). Exercises the
/// host's <c>AdminLoginService</c> directly with fakes: a resolved admin yields a session + login-success audit +
/// cookies + redirect to the allowlisted returnTo; a suspended/unknown caller yields no session + a denied audit
/// + an error redirect. The OIDC protocol layer (PKCE/state/nonce/code-exchange) is the framework's and is not
/// re-tested here.
/// </summary>
public sealed class AdminLoginServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 24, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid AdminId = Guid.Parse("a1111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData("/dashboard", "/dashboard")]     // allowlisted -> honored
    [InlineData("/merchants", "/merchants")]
    [InlineData("/evil", "/")]                    // not allowlisted -> default
    [InlineData("//evil.com", "/")]              // protocol-relative -> default
    [InlineData("https://evil.com", "/")]        // absolute -> default
    [InlineData("", "/")]                         // empty -> default
    [InlineData(null, "/")]
    public void ReturnUrl_is_only_honored_when_same_origin_and_allowlisted(string? requested, string expected)
    {
        string[] allowlist = ["/", "/dashboard", "/merchants"];
        Assert.Equal(expected, ApiHost::Api.ReturnUrlPolicy.Resolve(requested, allowlist, "/"));
    }

    [Fact]
    public async Task A_resolved_admin_gets_a_session_login_audit_cookies_and_a_returnTo_redirect()
    {
        var (service, store, audit, http) = Build(
            new ResolveResult(ResolveOutcome.Resolved,
                new Resolution(AdminId, "ops@org.com", Tier.Super, AccessibleMerchants.All)));

        await service.EstablishSessionAsync(http, "google-sub-1", "ops@org.com", emailVerified: true, "/dashboard", default);

        var session = Assert.Single(store.Added);
        Assert.Equal(AdminId, session.PlatformUserId);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(1, store.SaveCount);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.LoginSuccess && a.PlatformUserId == AdminId);
        Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
        Assert.Equal("/dashboard", http.Response.Headers.Location);
        Assert.Contains(http.Response.Headers.SetCookie, c => c!.Contains("adm_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_suspended_admin_gets_no_session_a_denied_audit_and_an_error_redirect()
    {
        var (service, store, audit, http) = Build(ResolveResult.Suspended);

        await service.EstablishSessionAsync(http, "google-sub-2", "ops@org.com", emailVerified: true, "/dashboard", default);

        Assert.Empty(store.Added);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "suspended");
        Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
        Assert.Equal("/login-error?reason=suspended", http.Response.Headers.Location);
        Assert.DoesNotContain(http.Response.Headers.SetCookie, c => c!.Contains("adm_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_not_allowlisted_caller_is_denied_not_provisioned()
    {
        // the resolver returns NotFound (not an existing admin, not invited, not allowlisted)
        var (service, store, audit, http) = Build(ResolveResult.NotFound);

        await service.EstablishSessionAsync(http, "google-sub-3", "stranger@org.com", emailVerified: true, "/", default);

        Assert.Empty(store.Added);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "not-provisioned");
    }

    [Fact]
    public async Task A_missing_subject_is_denied_before_any_resolution()
    {
        var (service, store, audit, http) = Build(ResolveResult.NotFound);

        await service.EstablishSessionAsync(http, subject: null, email: "x@org.com", emailVerified: true, returnTo: "/", ct: default);

        Assert.Empty(store.Added);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "missing-subject");
    }

    // --- harness ---

    private static (LoginService, FakeSessionStore, FakeAuthAudit, DefaultHttpContext) Build(ResolveResult resolve)
    {
        var store = new FakeSessionStore();
        var audit = new FakeAuthAudit();
        var cookies = new SessionCookies(Options.Create(new AdminSessionOptions()), new Env());
        var sessionOptions = Options.Create(new AdminSessionOptions { ReturnUrlAllowlist = ["/", "/dashboard", "/merchants"] });
        var oidcOptions = Options.Create(new AdminAuthOptions { ErrorPath = "/login-error" });
        var provider = new ServiceCollection()
            .AddScoped<IAuthAuditWriter>(_ => audit) // DenyAsync resolves the audit writer on a fresh scope
            .BuildServiceProvider();

        var service = new LoginService(new FakeResolver(resolve), store, audit, cookies, new TestClock(Now),
            provider.GetRequiredService<IServiceScopeFactory>(), sessionOptions, oidcOptions,
            NullLogger<LoginService>.Instance);

        var http = new DefaultHttpContext();
        http.Request.IsHttps = true;
        return (service, store, audit, http);
    }

    private sealed class FakeResolver(ResolveResult result) : ICallbackResolver
    {
        public Task<ResolveResult> ResolveAtCallbackAsync(string subject, string email, bool emailVerified, string correlationId, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public readonly List<Session> Added = [];
        public int SaveCount;
        public void Add(Session session) => Added.Add(session);
        public Task<int> SaveChangesAsync(CancellationToken ct) { SaveCount++; return Task.FromResult(1); }
        public Task<Session?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) => Task.FromResult<Session?>(null);
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) => Task.FromResult(false);
        public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeAllForAdminAsync(Guid adminId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyList<Session>> ListByAdminAsync(Guid adminAccountId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Session>>([]);
        public Task<Session?> FindByIdAsync(Guid sessionId, CancellationToken ct) => Task.FromResult<Session?>(null);
    }

    private sealed class FakeAuthAudit : IAuthAuditWriter
    {
        public readonly List<AuthAudit> Appended = [];
        public void Append(AuthAudit entry) => Appended.Add(entry);
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
    }

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production; // not dev-http -> real cookie attrs
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
