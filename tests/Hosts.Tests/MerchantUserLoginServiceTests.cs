extern alias ApiHost;
using ApiHost::Api;
using ApiHost::Api.Merchants;
using BuildingBlocks.Application;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SharedKernel;

namespace Hosts.Tests;

/// <summary>
/// Callback-time 4-way state branch for the merchant-user BFF (REQ-9.4). Exercises the host's
/// <c>MerchantUserLoginService</c> directly with fakes: an Active merchant user yields a session + login-success
/// audit + cookies + redirect to the allowlisted returnTo; an unknown subject yields a stateless Registration
/// ticket + redirect to /register (no session, no self-provision — REQ-9.6); a Rejected user yields a Correction
/// ticket; a PendingApproval user yields a 403 with no session; a Suspended user / any failure yields no session
/// + a denied audit + an error redirect. The wire ticket is a signed+time-limited token (no server row); the
/// tests decode it to check identity/purpose. The OIDC protocol layer (PKCE/state/nonce/code-exchange) is the
/// framework's and is not re-tested here.
/// </summary>
public sealed class MerchantUserLoginServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    private static readonly Guid MerchantId = Guid.Parse("d2222222-2222-2222-2222-222222222222");
    private const string RegisterUrl = "https://merchant-user-spa.example/register";

    [Fact]
    public async Task An_active_merchant_user_gets_a_session_login_audit_cookies_and_a_returnTo_redirect()
    {
        var (service, ctx) = Build(LoginResult.Active(
            new Resolution(UserId, "p@org.com", MerchantId, new HashSet<string> { "payment.create" })));

        await service.HandleCallbackAsync(ctx.Http, "google-sub-1", "p@org.com", null, "google", "/dashboard", default);

        var session = Assert.Single(ctx.Sessions.Added);
        Assert.Equal(UserId, session.UserId);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.Equal(Now.AddHours(24), session.IdleExpiresAt);
        Assert.Equal(Now.AddDays(7), session.AbsoluteExpiresAt);
        Assert.Equal(1, ctx.Sessions.SaveCount);
        Assert.Contains(ctx.Audit.Appended, a => a.EventType == AuthEventType.LoginSuccess && a.UserId == UserId);
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/dashboard", ctx.Http.Response.Headers.Location);
        Assert.Contains(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("mch_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_subject_gets_a_registration_ticket_and_a_redirect_to_register_no_session()
    {
        var (service, ctx) = Build(LoginResult.NotFound);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-new", "new@org.com", "org.com", "google", "/", default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.StartsWith(RegisterUrl + "?ticket=", ctx.Http.Response.Headers.Location.ToString());
        var payload = ctx.DecodeMintedTicket();
        Assert.Equal(TicketPurpose.Registration, payload.Purpose);
        Assert.Equal("new@org.com", payload.Email);
        Assert.Equal("google-sub-new", payload.Subject);
        Assert.NotEqual(Guid.Empty, payload.OperationId);
        Assert.DoesNotContain(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("mch_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_user_gets_a_correction_ticket_and_a_redirect_to_register()
    {
        var (service, ctx) = Build(LoginResult.Rejected);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-rej", "rej@org.com", null, "google", "/", default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.StartsWith(RegisterUrl + "?ticket=", ctx.Http.Response.Headers.Location.ToString());
        Assert.Equal(TicketPurpose.Correction, ctx.DecodeMintedTicket().Purpose);
    }

    [Fact]
    public async Task A_repeated_callback_for_the_same_subject_just_mints_a_fresh_ticket_no_state()
    {
        var (service, ctx) = Build(LoginResult.NotFound);

        // With no server-side ticket row, a repeated callback for the same subject is harmless: it simply mints
        // another fresh, self-expiring token and redirects to /register — no error, no "pending" state.
        await service.HandleCallbackAsync(ctx.Http, "google-sub-dup", "dup@org.com", null, "google", "/", default);
        ctx.Http.Response.Clear();
        await service.HandleCallbackAsync(ctx.Http, "google-sub-dup", "dup@org.com", null, "google", "/", default);

        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.StartsWith(RegisterUrl + "?ticket=", ctx.Http.Response.Headers.Location.ToString());
        Assert.Equal(TicketPurpose.Registration, ctx.DecodeMintedTicket().Purpose);
        Assert.DoesNotContain(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("mch_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_pending_user_is_redirected_with_awaiting_approval_reason_no_session_no_ticket()
    {
        var (service, ctx) = Build(LoginResult.Pending);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-pend", "pend@org.com", null, "google", "/", default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/login-error?reason=awaiting-approval", ctx.Http.Response.Headers.Location);
        Assert.DoesNotContain(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("mch_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_suspended_user_gets_no_session_a_denied_audit_and_an_error_redirect()
    {
        var (service, ctx) = Build(LoginResult.Suspended);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-susp", "susp@org.com", null, "google", "/", default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.Contains(ctx.Audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "suspended");
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/login-error?reason=suspended", ctx.Http.Response.Headers.Location);
    }

    [Fact]
    public async Task A_missing_identity_is_denied_before_any_resolution()
    {
        var (service, ctx) = Build(LoginResult.NotFound);

        await service.HandleCallbackAsync(ctx.Http, subject: null, email: "x@org.com", hostedDomain: null, provider: "google", returnTo: "/", ct: default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.Contains(ctx.Audit.Appended, a => a.EventType == AuthEventType.AuthDenied && a.Reason == "missing-identity");
    }

    // --- harness ---

    private sealed record Ctx(DefaultHttpContext Http, FakeSessionStore Sessions, FakeAuthAudit Audit,
        UserRegistrationTickets Protector)
    {
        /// <summary>Decodes the signed ticket carried in the redirect Location's <c>ticket</c> query param.</summary>
        public UserTicketPayload DecodeMintedTicket()
        {
            var location = Http.Response.Headers.Location.ToString();
            var query = QueryHelpers.ParseQuery(location[location.IndexOf('?')..]);
            Assert.True(Protector.TryUnprotect(query["ticket"].ToString(), out var payload));
            return payload;
        }
    }

    // Mirrors the admin WebAppBaseUrl behavior: with the callback landing on the API origin, both the post-login
    // returnTo and the error redirect must become absolute to the merchant-user SPA origin.
    [Fact]
    public async Task With_WebAppBaseUrl_the_returnTo_and_error_redirects_are_absolute_to_the_web_app_origin()
    {
        var (service, ctx) = Build(LoginResult.Active(
            new Resolution(UserId, "p@org.com", MerchantId, new HashSet<string>())), spaBaseUrl: "https://localhost:3002");
        await service.HandleCallbackAsync(ctx.Http, "google-sub-1", "p@org.com", null, "google", "/dashboard", default);
        Assert.Equal("https://localhost:3002/dashboard", ctx.Http.Response.Headers.Location);

        var (pending, pendingCtx) = Build(LoginResult.Pending, spaBaseUrl: "https://localhost:3002");
        await pending.HandleCallbackAsync(pendingCtx.Http, "google-sub-2", "p@org.com", null, "google", "/", default);
        Assert.Equal("https://localhost:3002/login-error?reason=awaiting-approval", pendingCtx.Http.Response.Headers.Location);

        // The committed RegisterUrl default is now RELATIVE ("/register") — the ticket redirect must go absolute
        // too, or production would 404 the applicant on the API origin.
        var (applicant, applicantCtx) = Build(LoginResult.NotFound, spaBaseUrl: "https://localhost:3002", registerUrl: "/register");
        await applicant.HandleCallbackAsync(applicantCtx.Http, "google-sub-3", "new@org.com", null, "google", "/", default);
        Assert.StartsWith("https://localhost:3002/register?ticket=", applicantCtx.Http.Response.Headers.Location.ToString());
    }

    private static (UserLoginService, Ctx) Build(LoginResult resolve, string spaBaseUrl = "", string registerUrl = RegisterUrl)
    {
        var sessions = new FakeSessionStore();
        var audit = new FakeAuthAudit();
        var env = new Env();
        var registrationOptions = Options.Create(new UserRegistrationOptions());
        var ticketProtector = new UserRegistrationTickets(new EphemeralDataProtectionProvider(), registrationOptions);
        var cookies = new UserSessionCookies(Options.Create(new UserSessionOptions()), env);
        var sessionOptions = Options.Create(new UserSessionOptions
        {
            ReturnUrlAllowlist = ["/", "/dashboard"],
            WebAppBaseUrl = spaBaseUrl,
        });
        var oidcOptions = Options.Create(new UserOidcOptions { ErrorPath = "/login-error", RegisterUrl = registerUrl });
        var provider = new ServiceCollection()
            .AddScoped<IAuthAuditWriter>(_ => audit) // DenyAsync resolves the audit writer on a fresh scope
            .BuildServiceProvider();

        var service = new UserLoginService(
            new FakeResolver(resolve), sessions, audit, ticketProtector, cookies,
            new TestClock(Now), provider.GetRequiredService<IServiceScopeFactory>(),
            sessionOptions, oidcOptions, NullLogger<UserLoginService>.Instance);

        var http = new DefaultHttpContext();
        http.Request.IsHttps = true;
        return (service, new Ctx(http, sessions, audit, ticketProtector));
    }

    private sealed class FakeResolver(LoginResult result) : IUserCallbackResolver
    {
        public Task<LoginResult> ResolveAtCallbackAsync(ProviderIdentity identity, CancellationToken ct) => Task.FromResult(result);
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
        public Task RevokeAllForUserAsync(Guid merchantUserId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
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
