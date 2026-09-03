extern alias ApiHost;
using ApiHost::Api;
using ApiHost::Api.Admins;
using Admins.Application;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel;

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
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly string[] SensitiveCanaries =
    [
        "privacy.canary@viriyah.co.th",
        TenantId.ToString("D"),
        ObjectId.ToString("D"),
        "authorization-code-canary",
        "id-token-canary",
        "access-token-canary",
        "cookie-canary",
        "session-token-canary",
    ];

    [Fact]
    public void Login_properties_preserve_returnTo_in_a_dedicated_protected_state_item()
    {
        var properties = OidcAuthentication.CreateLoginProperties("/scalar");

        Assert.Equal("/scalar", properties.Items[OidcAuthentication.ReturnToPropertyKey]);
    }

    [Fact]
    public void Callback_prefers_the_dedicated_returnTo_item_over_redirect_uri_fallback()
    {
        var properties = new AuthenticationProperties { RedirectUri = "/dashboard" };
        properties.Items[OidcAuthentication.ReturnToPropertyKey] = "/scalar";

        Assert.Equal("/scalar", OidcAuthentication.GetReturnTo(properties));
    }

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

        await service.EstablishSessionAsync(http, "google", "google-sub-1", employeeId: null, "/dashboard", default);

        var session = Assert.Single(store.Added);
        Assert.Equal(AdminId, session.AdminUserId);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(Now.AddHours(24), session.IdleExpiresAt);
        Assert.Equal(Now.AddDays(7), session.AbsoluteExpiresAt);
        Assert.Equal(1, store.SaveCount);
        Assert.Contains(audit.Appended, a =>
            a.EventType == AuthEventType.LoginSuccess && a.AdminUserId == AdminId && a.Subject == "google-sub-1");
        Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
        Assert.Equal("/dashboard", http.Response.Headers.Location);
        Assert.Contains(http.Response.Headers.SetCookie, c => c!.Contains("adm_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_suspended_admin_gets_no_session_a_denied_audit_and_an_error_redirect()
    {
        var (service, store, audit, http) = Build(ResolveResult.Suspended);

        await service.EstablishSessionAsync(http, "google", "google-sub-2", employeeId: null, "/dashboard", default);

        Assert.Empty(store.Added);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "suspended");
        Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
        Assert.Equal("/login-error?reason=suspended", http.Response.Headers.Location);
        Assert.DoesNotContain(http.Response.Headers.SetCookie, c => c!.Contains("adm_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_identity_conflict_gets_a_typed_error_without_a_session()
    {
        var (service, store, audit, http) = Build(ResolveResult.IdentityConflict);

        await service.EstablishMicrosoftSessionAsync(
            http, WorkforceClaims(), "/dashboard", default);

        Assert.Empty(store.Added);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "identity-conflict");
        Assert.Equal("/login-error?reason=identity-conflict", http.Response.Headers.Location);
    }

    [Fact]
    public async Task An_unknown_not_allowlisted_caller_is_denied_not_provisioned()
    {
        // the resolver returns NotFound (not an existing admin, not invited, not allowlisted)
        var (service, store, audit, http) = Build(ResolveResult.NotFound);

        await service.EstablishSessionAsync(http, "google", "google-sub-3", employeeId: null, "/", default);

        Assert.Empty(store.Added);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "not-provisioned");
    }

    // tier0-graph-employee-profile REQ-2.17, 3.4-3.5, 3.19: the four profile outcomes map to their browser reasons;
    // the audit keeps the internal reason when the result carries one, otherwise the browser reason.
    [Theory]
    [InlineData(ResolveOutcome.EmployeeProfileMissing, null, "employee-profile-missing", "employee-profile-missing")]
    [InlineData(ResolveOutcome.EmployeeProfileInvalid, null, "employee-profile-invalid", "employee-profile-invalid")]
    [InlineData(ResolveOutcome.EmployeeProfileUnmapped, null, "employee-profile-unmapped", "employee-profile-unmapped")]
    [InlineData(ResolveOutcome.EmployeeProfileUnavailable, "hr-source-unavailable", "employee-profile-unavailable", "hr-source-unavailable")]
    [InlineData(ResolveOutcome.IdentityConflict, "employee-mismatch", "identity-conflict", "employee-mismatch")]
    [InlineData(ResolveOutcome.IdentityConflict, "employee-taken", "identity-conflict", "employee-taken")]
    [InlineData(ResolveOutcome.IdentityConflict, null, "identity-conflict", "identity-conflict")]
    public async Task Employee_profile_outcomes_map_browser_reason_and_internal_audit_reason(
        ResolveOutcome outcome, string? denialReason, string browserReason, string auditReason)
    {
        var (service, store, audit, http) = Build(new ResolveResult(outcome, null, denialReason));

        await service.EstablishMicrosoftSessionAsync(
            http, WorkforceClaims(employeeId: "ZTEST1"), "/dashboard", default);

        Assert.Empty(store.Added);
        var denied = Assert.Single(audit.Appended, a => a.EventType == AuthEventType.AuthDenied);
        Assert.Equal(auditReason, denied.Reason);
        Assert.Null(denied.Subject);
        Assert.Equal($"/login-error?reason={browserReason}", http.Response.Headers.Location);
        Assert.DoesNotContain("ZTEST1", http.Response.Headers.Location.ToString(), StringComparison.Ordinal); // REQ-9.7
    }

    /// <summary>Exhaustiveness guard: every ResolveOutcome except Resolved must map to a browser reason (a new member
    /// that reaches the discard arm throws here instead of shipping as "not-provisioned").</summary>
    [Fact]
    public async Task Every_resolve_outcome_is_mapped_to_a_browser_reason()
    {
        foreach (var outcome in Enum.GetValues<ResolveOutcome>().Where(o => o != ResolveOutcome.Resolved))
        {
            var (service, store, audit, http) = Build(new ResolveResult(outcome, null));
            await service.EstablishMicrosoftSessionAsync(http, WorkforceClaims(), "/", default);
            Assert.Empty(store.Added);
            var denied = Assert.Single(audit.Appended, a => a.EventType == AuthEventType.AuthDenied);
            Assert.False(string.IsNullOrWhiteSpace(denied.Reason));
            Assert.StartsWith("/login-error?reason=", http.Response.Headers.Location.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Employee_id_is_forwarded_to_the_resolver_verbatim()
    {
        var resolver = new RecordingResolver();
        var (service, _, _, http) = Build(ResolveResult.NotFound, resolver: resolver);

        await service.EstablishMicrosoftSessionAsync(http, WorkforceClaims(employeeId: "AB12"), "/", default);
        Assert.Equal("AB12", resolver.EmployeeId);
        Assert.Equal(TenantId, resolver.TenantId);
        Assert.Equal(ObjectId, resolver.ObjectId);
        Assert.Equal("employee@viriyah.co.th", resolver.Email);

        await service.EstablishMicrosoftSessionAsync(http, WorkforceClaims(email: null), "/", default);
        Assert.Null(resolver.EmployeeId);
        Assert.Null(resolver.Email);
        Assert.Equal(2, resolver.MicrosoftCalls);
        Assert.Equal(0, resolver.GenericCalls);
    }

    [Fact]
    public async Task A_missing_subject_is_denied_before_any_resolution()
    {
        var (service, store, audit, http) = Build(ResolveResult.NotFound);

        await service.EstablishSessionAsync(
            http, provider: "google", subject: null, employeeId: null, returnTo: "/", ct: default);

        Assert.Empty(store.Added);
        Assert.Contains(audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "missing-subject");
    }

    [Fact]
    public async Task Generic_session_seam_rejects_microsoft_before_resolution()
    {
        var resolver = new RecordingResolver();
        var (service, store, audit, http) = Build(ResolveResult.NotFound, resolver: resolver);

        await service.EstablishSessionAsync(
            http, "MICROSOFT", ObjectId.ToString("D"), employeeId: null, returnTo: "/", ct: default);

        Assert.Equal(0, resolver.GenericCalls);
        Assert.Equal(0, resolver.MicrosoftCalls);
        Assert.Empty(store.Added);
        var denied = Assert.Single(audit.Appended);
        Assert.Equal(AuthEventType.AuthDenied, denied.EventType);
        Assert.Equal("workforce-access-denied", denied.Reason);
        Assert.Null(denied.Subject);
    }

    [Fact]
    public async Task Microsoft_success_audit_omits_external_subject()
    {
        var (service, _, audit, http) = Build(
            ResolveResult.Of(new Resolution(
                AdminId, "ops@org.com", Tier.Scoped, AccessibleMerchants.Of(new HashSet<Guid>()))));

        await service.EstablishMicrosoftSessionAsync(
            http, WorkforceClaims(email: "ops@viriyah.co.th"), "/dashboard", default);

        var entry = Assert.Single(audit.Appended, a => a.EventType == AuthEventType.LoginSuccess);
        Assert.Null(entry.Subject);
    }

    [Fact]
    public async Task Microsoft_denial_audit_omits_external_subject()
    {
        var (service, _, audit, http) = Build(ResolveResult.NotFound);

        await service.EstablishMicrosoftSessionAsync(
            http, WorkforceClaims(email: "ops@viriyah.co.th"), "/", default);

        var entry = Assert.Single(audit.Appended, a => a.EventType == AuthEventType.AuthDenied);
        Assert.Null(entry.Subject);
    }

    // With the provider callback landing on the API origin (provider-scoped OIDC), a configured WebAppBaseUrl makes
    // both the post-login returnTo and the error redirect absolute to the SPA origin — the browser must never
    // land on the API host's JSON 404.
    [Fact]
    public async Task With_WebAppBaseUrl_the_returnTo_and_error_redirects_are_absolute_to_the_web_app_origin()
    {
        var (service, _, _, http) = Build(
            new ResolveResult(ResolveOutcome.Resolved,
                new Resolution(AdminId, "ops@org.com", Tier.Super, AccessibleMerchants.All)),
            spaBaseUrl: "https://localhost:3001");
        await service.EstablishSessionAsync(http, "google", "google-sub-1", employeeId: null, "/dashboard", default);
        Assert.Equal("https://localhost:3001/dashboard", http.Response.Headers.Location);

        var (denied, _, _, deniedHttp) = Build(ResolveResult.Suspended, spaBaseUrl: "https://localhost:3001");
        await denied.EstablishSessionAsync(deniedHttp, "google", "google-sub-2", employeeId: null, "/", default);
        Assert.Equal("https://localhost:3001/login-error?reason=suspended", deniedHttp.Response.Headers.Location);
    }

    [Fact]
    public async Task With_ScalarBaseUrl_the_scalar_returnTo_is_absolute_to_the_api_origin()
    {
        var (service, _, _, http) = Build(
            new ResolveResult(ResolveOutcome.Resolved,
                new Resolution(AdminId, "ops@org.com", Tier.Super, AccessibleMerchants.All)),
            spaBaseUrl: "https://localhost:3001",
            scalarBaseUrl: "https://localhost:5001",
            allowlist: ["/", "/dashboard", "/scalar"],
            defaultReturnPath: "/dashboard");

        await service.EstablishSessionAsync(http, "google", "google-sub-1", employeeId: null, "/scalar", default);

        Assert.Equal("https://localhost:5001/scalar", http.Response.Headers.Location);
    }

    [Fact]
    public async Task An_unallowlisted_scalar_returnTo_keeps_the_spa_default_fallback()
    {
        var (service, _, _, http) = Build(
            new ResolveResult(ResolveOutcome.Resolved,
                new Resolution(AdminId, "ops@org.com", Tier.Super, AccessibleMerchants.All)),
            spaBaseUrl: "https://localhost:3001",
            defaultReturnPath: "/dashboard");

        await service.EstablishSessionAsync(http, "google", "google-sub-1", employeeId: null, "/scalar", default);

        Assert.Equal("https://localhost:3001/dashboard", http.Response.Headers.Location);
    }

    [Fact]
    public async Task Resolution_exception_log_audit_and_browser_reason_omit_sensitive_values()
    {
        var logger = new CapturingLogger<LoginService>();
        var resolver = new ThrowingResolver(new InvalidOperationException(string.Join('|', SensitiveCanaries)));
        var (service, store, audit, http) = Build(
            ResolveResult.NotFound, resolver: resolver, logger: logger);
        http.TraceIdentifier = "safe-correlation";

        await service.EstablishMicrosoftSessionAsync(
            http, WorkforceClaims(email: SensitiveCanaries[0]), "/", default);

        Assert.Empty(store.Added);
        var denied = Assert.Single(audit.Appended, entry => entry.EventType == AuthEventType.AuthDenied);
        Assert.Null(denied.Subject);
        Assert.Equal("resolve-failed", denied.Reason);
        Assert.Equal("/login-error?reason=resolve-failed", http.Response.Headers.Location);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("safe-correlation", StringComparison.Ordinal));
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        AssertNoCanaries(string.Join('\n', logger.Entries.Select(entry => entry.Message)));
        AssertNoCanaries(string.Join('\n', denied.Subject, denied.Reason, http.Response.Headers.Location));
    }

    [Fact]
    public async Task Session_write_exception_log_and_denied_audit_omit_sensitive_values()
    {
        var logger = new CapturingLogger<LoginService>();
        var (service, store, audit, http) = Build(
            ResolveResult.Of(new Resolution(
                AdminId, "privacy.canary@viriyah.co.th", Tier.Scoped,
                AccessibleMerchants.Of(new HashSet<Guid>()))),
            logger: logger);
        store.SaveFailure = new InvalidOperationException(string.Join('|', SensitiveCanaries));
        http.TraceIdentifier = "safe-session-correlation";

        await service.EstablishMicrosoftSessionAsync(
            http, WorkforceClaims(email: SensitiveCanaries[0]), "/", default);

        var denied = Assert.Single(audit.Appended, entry => entry.EventType == AuthEventType.AuthDenied);
        Assert.Null(denied.Subject);
        Assert.Equal("session-write-failed", denied.Reason);
        Assert.Equal("/login-error?reason=session-write-failed", http.Response.Headers.Location);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
        AssertNoCanaries(string.Join('\n', logger.Entries.Select(entry => entry.Message)));
    }

    // --- harness ---

    private static MicrosoftWorkforceClaims WorkforceClaims(
        string? email = "employee@viriyah.co.th", string? employeeId = null) =>
        new(TenantId, ObjectId, email, employeeId);

    private static (LoginService, FakeSessionStore, FakeAuthAudit, DefaultHttpContext) Build(
        ResolveResult resolve,
        string spaBaseUrl = "",
        string scalarBaseUrl = "",
        IReadOnlyCollection<string>? allowlist = null,
        string defaultReturnPath = "/",
        ICallbackResolver? resolver = null,
        ILogger<LoginService>? logger = null)
    {
        var store = new FakeSessionStore();
        var audit = new FakeAuthAudit();
        var cookies = new SessionCookies(Options.Create(new AdminSessionOptions()), new Env());
        var sessionOptions = Options.Create(new AdminSessionOptions
        {
            DefaultReturnPath = defaultReturnPath,
            ReturnUrlAllowlist = allowlist?.ToArray() ?? ["/", "/dashboard", "/merchants"],
            WebAppBaseUrl = spaBaseUrl,
            ScalarBaseUrl = scalarBaseUrl,
        });
        var oidcOptions = Options.Create(new AdminAuthOptions { ErrorPath = "/login-error" });
        var provider = new ServiceCollection()
            .AddScoped<IAuthAuditWriter>(_ => audit) // DenyAsync resolves the audit writer on a fresh scope
            .BuildServiceProvider();

        var service = new LoginService(resolver ?? new FakeResolver(resolve), store, audit, cookies, new TestClock(Now),
            provider.GetRequiredService<IServiceScopeFactory>(), sessionOptions, oidcOptions,
            logger ?? NullLogger<LoginService>.Instance);

        var http = new DefaultHttpContext();
        http.Request.IsHttps = true;
        return (service, store, audit, http);
    }

    private sealed class FakeResolver(ResolveResult result) : ICallbackResolver
    {
        public Task<ResolveResult> ResolveAtCallbackAsync(
            ProviderIdentity identity, string? employeeId, string correlationId, CancellationToken ct) =>
            Task.FromResult(result);

        public Task<ResolveResult> ResolveMicrosoftAtCallbackAsync(
            Guid tenantId, Guid objectId, string? email, string? employeeId,
            string correlationId, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class RecordingResolver : ICallbackResolver
    {
        public int GenericCalls { get; private set; }
        public int MicrosoftCalls { get; private set; }
        public Guid? TenantId { get; private set; }
        public Guid? ObjectId { get; private set; }
        public string? Email { get; private set; }
        public string? EmployeeId { get; private set; }

        public Task<ResolveResult> ResolveAtCallbackAsync(
            ProviderIdentity identity, string? employeeId, string correlationId, CancellationToken ct)
        {
            GenericCalls++;
            EmployeeId = employeeId;
            return Task.FromResult(ResolveResult.NotFound);
        }

        public Task<ResolveResult> ResolveMicrosoftAtCallbackAsync(
            Guid tenantId, Guid objectId, string? email, string? employeeId,
            string correlationId, CancellationToken ct)
        {
            MicrosoftCalls++;
            TenantId = tenantId;
            ObjectId = objectId;
            Email = email;
            EmployeeId = employeeId;
            return Task.FromResult(ResolveResult.NotFound);
        }
    }

    private sealed class ThrowingResolver(Exception error) : ICallbackResolver
    {
        public Task<ResolveResult> ResolveAtCallbackAsync(
            ProviderIdentity identity, string? employeeId, string correlationId, CancellationToken ct) =>
            Task.FromException<ResolveResult>(error);

        public Task<ResolveResult> ResolveMicrosoftAtCallbackAsync(
            Guid tenantId, Guid objectId, string? email, string? employeeId,
            string correlationId, CancellationToken ct) => Task.FromException<ResolveResult>(error);
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public readonly List<Session> Added = [];
        public int SaveCount;
        public Exception? SaveFailure;
        public void Add(Session session) => Added.Add(session);
        public Task<int> SaveChangesAsync(CancellationToken ct)
        {
            if (SaveFailure is not null)
                return Task.FromException<int>(SaveFailure);
            SaveCount++;
            return Task.FromResult(1);
        }
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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((formatter(state, exception), exception));
    }

    private static void AssertNoCanaries(string value)
    {
        foreach (var canary in SensitiveCanaries)
            Assert.DoesNotContain(canary, value, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production; // not dev-http -> real cookie attrs
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
