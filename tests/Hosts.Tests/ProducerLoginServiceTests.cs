extern alias ApiHost;
using ApiHost::Api;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Producer.Application;
using Producer.Domain;

namespace Hosts.Tests;

/// <summary>
/// Callback-time 4-way state branch for the producer BFF (REQ-9.4). Exercises the host's <c>ProducerLoginService</c>
/// directly with fakes: an Active producer yields a session + login-success audit + cookies + redirect to the
/// allowlisted returnTo; an unknown subject yields a Registration ticket + redirect to /register (no session, no
/// self-provision — REQ-9.6); a Rejected user yields a Correction ticket; a PendingApproval user yields a 403 with
/// no session; a Suspended user / any failure yields no session + a denied audit + an error redirect. The OIDC
/// protocol layer (PKCE/state/nonce/code-exchange) is the framework's and is not re-tested here.
/// </summary>
public sealed class ProducerLoginServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid UserId = Guid.Parse("c1111111-1111-1111-1111-111111111111");
    private static readonly Guid TenantId = Guid.Parse("d2222222-2222-2222-2222-222222222222");
    private const string RegisterUrl = "https://producer-spa.example/register";

    [Fact]
    public async Task An_active_producer_gets_a_session_login_audit_cookies_and_a_returnTo_redirect()
    {
        var (service, ctx) = Build(ProducerLoginResult.Active(
            new ProducerResolution(UserId, "p@org.com", TenantId, new HashSet<string> { "product.create" })));

        await service.HandleCallbackAsync(ctx.Http, "google-sub-1", "p@org.com", null, "/dashboard", default);

        var session = Assert.Single(ctx.Sessions.Added);
        Assert.Equal(UserId, session.ProducerAccountId);
        Assert.Equal(ProducerSessionStatus.Active, session.Status);
        Assert.Equal(1, ctx.Sessions.SaveCount);
        Assert.Contains(ctx.Audit.Appended, a => a.EventType == ProducerAuthEventType.LoginSuccess && a.ProducerAccountId == UserId);
        Assert.Empty(ctx.Tickets.Added);
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/dashboard", ctx.Http.Response.Headers.Location);
        Assert.Contains(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("prd_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unknown_subject_gets_a_registration_ticket_and_a_redirect_to_register_no_session()
    {
        var (service, ctx) = Build(ProducerLoginResult.NotFound);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-new", "new@org.com", "org.com", "/", default);

        Assert.Empty(ctx.Sessions.Added);
        var ticket = Assert.Single(ctx.Tickets.Added);
        Assert.Equal(TicketPurpose.Registration, ticket.Purpose);
        Assert.Equal("new@org.com", ticket.Email);
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.StartsWith(RegisterUrl + "?ticket=", ctx.Http.Response.Headers.Location.ToString());
        Assert.DoesNotContain(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("prd_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rejected_user_gets_a_correction_ticket_and_a_redirect_to_register()
    {
        var (service, ctx) = Build(ProducerLoginResult.Rejected);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-rej", "rej@org.com", null, "/", default);

        Assert.Empty(ctx.Sessions.Added);
        var ticket = Assert.Single(ctx.Tickets.Added);
        Assert.Equal(TicketPurpose.Correction, ticket.Purpose);
        Assert.StartsWith(RegisterUrl + "?ticket=", ctx.Http.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task A_repeated_callback_for_the_same_subject_is_blocked_and_mints_no_second_ticket()
    {
        var (service, ctx) = Build(ProducerLoginResult.NotFound);

        // 1st callback issues the ticket; 2nd (same subject, before expiry) must be blocked.
        await service.HandleCallbackAsync(ctx.Http, "google-sub-dup", "dup@org.com", null, "/", default);
        ctx.Http.Response.Clear();
        await service.HandleCallbackAsync(ctx.Http, "google-sub-dup", "dup@org.com", null, "/", default);

        Assert.Single(ctx.Tickets.Added); // still exactly one row, not two
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/login-error?reason=registration-pending", ctx.Http.Response.Headers.Location);
        Assert.DoesNotContain(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("prd_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_callback_with_a_new_subject_but_a_pending_email_is_blocked()
    {
        var (service, ctx) = Build(ProducerLoginResult.NotFound);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-a", "shared@org.com", null, "/", default);
        ctx.Http.Response.Clear();
        await service.HandleCallbackAsync(ctx.Http, "google-sub-b", "shared@org.com", null, "/", default);

        Assert.Single(ctx.Tickets.Added); // email match alone blocks the duplicate
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/login-error?reason=registration-pending", ctx.Http.Response.Headers.Location);
    }

    [Fact]
    public async Task An_expired_pending_ticket_does_not_block_a_fresh_ticket()
    {
        var (service, ctx) = Build(ProducerLoginResult.NotFound);
        // Seed a ticket that expired before Now (issued 2h ago, 10-min TTL) — not pending, must not block re-issue.
        ctx.Tickets.Seeded.Add(RegistrationTicket.Issue(
            "google-sub-exp", "exp@org.com", null, TicketPurpose.Registration, Now.AddHours(-2), TimeSpan.FromMinutes(10)));

        await service.HandleCallbackAsync(ctx.Http, "google-sub-exp", "exp@org.com", null, "/", default);

        var ticket = Assert.Single(ctx.Tickets.Added);
        Assert.Equal(TicketPurpose.Registration, ticket.Purpose);
        Assert.StartsWith(RegisterUrl + "?ticket=", ctx.Http.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task A_pending_user_is_redirected_with_awaiting_approval_reason_no_session_no_ticket()
    {
        var (service, ctx) = Build(ProducerLoginResult.Pending);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-pend", "pend@org.com", null, "/", default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.Empty(ctx.Tickets.Added);
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/login-error?reason=awaiting-approval", ctx.Http.Response.Headers.Location);
        Assert.DoesNotContain(ctx.Http.Response.Headers.SetCookie, c => c!.Contains("prd_session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_suspended_user_gets_no_session_a_denied_audit_and_an_error_redirect()
    {
        var (service, ctx) = Build(ProducerLoginResult.Suspended);

        await service.HandleCallbackAsync(ctx.Http, "google-sub-susp", "susp@org.com", null, "/", default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.Contains(ctx.Audit.Appended, a => a.EventType == ProducerAuthEventType.AuthDenied && a.Reason == "suspended");
        Assert.Equal(StatusCodes.Status302Found, ctx.Http.Response.StatusCode);
        Assert.Equal("/login-error?reason=suspended", ctx.Http.Response.Headers.Location);
    }

    [Fact]
    public async Task A_missing_identity_is_denied_before_any_resolution()
    {
        var (service, ctx) = Build(ProducerLoginResult.NotFound);

        await service.HandleCallbackAsync(ctx.Http, subject: null, email: "x@org.com", hostedDomain: null, returnTo: "/", default);

        Assert.Empty(ctx.Sessions.Added);
        Assert.Empty(ctx.Tickets.Added);
        Assert.Contains(ctx.Audit.Appended, a => a.EventType == ProducerAuthEventType.AuthDenied && a.Reason == "missing-identity");
    }

    // --- harness ---

    private sealed record Ctx(DefaultHttpContext Http, FakeSessionStore Sessions, FakeAuthAudit Audit, FakeTickets Tickets);

    private static (ProducerLoginService, Ctx) Build(ProducerLoginResult resolve)
    {
        var sessions = new FakeSessionStore();
        var audit = new FakeAuthAudit();
        var tickets = new FakeTickets();
        var env = new Env();
        var registrationOptions = Options.Create(new ProducerRegistrationOptions());
        var ticketProtector = new ProducerRegistrationTickets(new EphemeralDataProtectionProvider(), registrationOptions);
        var cookies = new ProducerSessionCookies(Options.Create(new ProducerSessionOptions()), env);
        var sessionOptions = Options.Create(new ProducerSessionOptions { ReturnUrlAllowlist = ["/", "/dashboard"] });
        var oidcOptions = Options.Create(new ProducerOidcOptions { ErrorPath = "/login-error", RegisterUrl = RegisterUrl });
        var provider = new ServiceCollection()
            .AddScoped<IProducerAuthAuditWriter>(_ => audit) // DenyAsync resolves the audit writer on a fresh scope
            .BuildServiceProvider();

        var service = new ProducerLoginService(
            new FakeResolver(resolve), sessions, audit, tickets, new FakeTicketUnitOfWork(), ticketProtector, cookies,
            new TestClock(Now), provider.GetRequiredService<IServiceScopeFactory>(),
            sessionOptions, oidcOptions, registrationOptions, NullLogger<ProducerLoginService>.Instance);

        var http = new DefaultHttpContext();
        http.Request.IsHttps = true;
        return (service, new Ctx(http, sessions, audit, tickets));
    }

    private sealed class FakeResolver(ProducerLoginResult result) : IProducerCallbackResolver
    {
        public Task<ProducerLoginResult> ResolveAtCallbackAsync(string subject, CancellationToken ct) => Task.FromResult(result);
    }

    private sealed class FakeSessionStore : IProducerSessionStore
    {
        public readonly List<ProducerSession> Added = [];
        public int SaveCount;
        public void Add(ProducerSession session) => Added.Add(session);
        public Task<int> SaveChangesAsync(CancellationToken ct) { SaveCount++; return Task.FromResult(1); }
        public Task<ProducerSession?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) => Task.FromResult<ProducerSession?>(null);
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => Task.FromResult<Guid?>(null);
        public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) => Task.FromResult(false);
        public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => Task.CompletedTask;
        public Task RevokeAllForUserAsync(Guid tenantUserId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class FakeAuthAudit : IProducerAuthAuditWriter
    {
        public readonly List<ProducerAuthAudit> Appended = [];
        public void Append(ProducerAuthAudit entry) => Appended.Add(entry);
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
    }

    private sealed class FakeTickets : IRegistrationTicketRepository
    {
        public readonly List<RegistrationTicket> Added = [];
        public readonly List<RegistrationTicket> Seeded = []; // already-persisted rows the guard queries against
        public void Add(RegistrationTicket ticket) => Added.Add(ticket);
        public Task<bool> HasPendingAsync(string subject, string email, DateTime now, CancellationToken ct) =>
            Task.FromResult(Added.Concat(Seeded).Any(t =>
                t.UsedAt == null && t.ExpiresAt > now && (t.Subject == subject || t.Email == email)));
        public Task<bool> TryConsumeAsync(Guid id, TicketPurpose purpose, DateTime now, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeTicketUnitOfWork : IProducerRegistrationUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            operation(ct);
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
