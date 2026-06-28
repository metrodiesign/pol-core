using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Producer.Application;
using Producer.Domain;

namespace Api;

/// <summary>Resolves the producer at callback time (REQ-9.4) — a pure lookup behind the source-generated mediator so
/// the 4-way branch policy in <see cref="ProducerLoginService"/> can be unit-tested without it (mirrors
/// <c>IAdminCallbackResolver</c>). The callback NEVER self-provisions (REQ-9.6).</summary>
internal interface IProducerCallbackResolver
{
    Task<ProducerLoginResult> ResolveAtCallbackAsync(string subject, CancellationToken cancellationToken);
}

internal sealed class ProducerCallbackResolver(IMediator mediator) : IProducerCallbackResolver
{
    public Task<ProducerLoginResult> ResolveAtCallbackAsync(string subject, CancellationToken cancellationToken) =>
        mediator.Send(new ResolveLoginQuery(subject), cancellationToken).AsTask();
}

/// <summary>
/// Callback-time state branch for the producer BFF (REQ-9.4). On a verified Google identity it resolves the
/// TenantUser and branches FOUR ways: <b>Active</b> → start a server session (session + login-success audit in ONE
/// keyed pol_admin tx) + cookies + redirect to the allowlisted returnTo; <b>NotFound</b> → mint a Registration
/// ticket (server row + signed wire token) + redirect to the SPA register page; <b>Rejected</b> → mint a Correction
/// ticket + redirect to register (REQ-5.2); <b>PendingApproval</b> → 403 "awaiting approval" with no session
/// (REQ-22.5). A Suspended account (or any failure) gets no session and an error redirect. Every denial writes a
/// denied-auth audit on a FRESH scope so a half-built session can never be committed by the audit save (REQ-9.5/21.2).
/// No secret, token, code, raw session id, or ticket is ever logged (REQ-14.3).
/// </summary>
// ponytail: DUPLICATE-shaped of AdminLoginService (4-way producer branch + ticket mint, NO self-provision) — deliberate.
internal sealed class ProducerLoginService
{
    private readonly IProducerCallbackResolver _resolver;
    private readonly IProducerSessionStore _sessions;
    private readonly IProducerAuthAuditWriter _audit;
    private readonly IRegistrationTicketRepository _tickets;
    private readonly IProducerRegistrationUnitOfWork _ticketUnitOfWork;
    private readonly ProducerRegistrationTickets _ticketProtector;
    private readonly ProducerSessionCookies _cookies;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProducerSessionOptions _session;
    private readonly ProducerOidcOptions _oidc;
    private readonly ProducerRegistrationOptions _registration;
    private readonly ILogger<ProducerLoginService> _logger;

    public ProducerLoginService(
        IProducerCallbackResolver resolver,
        IProducerSessionStore sessions,
        IProducerAuthAuditWriter audit,
        IRegistrationTicketRepository tickets,
        IProducerRegistrationUnitOfWork ticketUnitOfWork,
        ProducerRegistrationTickets ticketProtector,
        ProducerSessionCookies cookies,
        IClock clock,
        IServiceScopeFactory scopeFactory,
        IOptions<ProducerSessionOptions> session,
        IOptions<ProducerOidcOptions> oidc,
        IOptions<ProducerRegistrationOptions> registration,
        ILogger<ProducerLoginService> logger)
    {
        _resolver = resolver;
        _sessions = sessions;
        _audit = audit;
        _tickets = tickets;
        _ticketUnitOfWork = ticketUnitOfWork;
        _ticketProtector = ticketProtector;
        _cookies = cookies;
        _clock = clock;
        _scopeFactory = scopeFactory;
        _session = session.Value;
        _oidc = oidc.Value;
        _registration = registration.Value;
        _logger = logger;
    }

    private ProducerSessionPolicy Policy => new(
        TimeSpan.FromMinutes(_session.IdleMinutes),
        TimeSpan.FromHours(_session.AbsoluteHours),
        TimeSpan.FromMinutes(_session.RotationMinutes),
        TimeSpan.FromSeconds(_session.GraceSeconds));

    /// <summary>Branches the callback on the producer's lifecycle state (REQ-9.4). Identity is the verified id_token's
    /// (REQ-9.3); the form/request can never override it.</summary>
    public async Task HandleCallbackAsync(
        HttpContext http, string? subject, string? email, string? hostedDomain, string? returnTo, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email))
        {
            await DenyAsync(http, "missing-identity", subject, ct);
            return;
        }

        ProducerLoginResult result;
        try
        {
            result = await _resolver.ResolveAtCallbackAsync(subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Producer resolution failed at callback.");
            await DenyAsync(http, "resolve-failed", subject, ct);
            return;
        }

        switch (result.Outcome)
        {
            case ProducerLoginOutcome.Active:
                await EstablishSessionAsync(http, result.Resolution!, subject, returnTo, ct);
                break;
            case ProducerLoginOutcome.NotFound:
                await IssueTicketAndRedirectAsync(http, subject, email, hostedDomain, TicketPurpose.Registration, ct);
                break;
            case ProducerLoginOutcome.Rejected:
                await IssueTicketAndRedirectAsync(http, subject, email, hostedDomain, TicketPurpose.Correction, ct);
                break;
            case ProducerLoginOutcome.PendingApproval:
                await RespondAwaitingApprovalAsync(http, ct);
                break;
            case ProducerLoginOutcome.Suspended:
            default:
                await DenyAsync(http, "suspended", subject, ct);
                break;
        }
    }

    /// <summary>Active → opens a server session (REQ-10.1). Session + login-success audit commit TOGETHER on the
    /// request's keyed pol_admin context (no partial). Any failure after resolution → no partial session, a denied
    /// audit on a fresh scope + an error redirect, never a 500 (REQ-9.5).</summary>
    private async Task EstablishSessionAsync(
        HttpContext http, ProducerResolution resolution, string subject, string? returnTo, CancellationToken ct)
    {
        try
        {
            var sessionToken = ProducerTokens.NewOpaqueToken();
            var csrfToken = ProducerTokens.NewOpaqueToken();
            var session = ProducerSession.Start(resolution.TenantUserId, ProducerTokens.Hash(sessionToken),
                _clock.UtcNow, Policy,
                http.Connection.RemoteIpAddress?.ToString(),
                Truncate(http.Request.Headers.UserAgent.ToString(), 256));

            _sessions.Add(session);
            _audit.Append(ProducerAuthAudit.For(ProducerAuthEventType.LoginSuccess, http.TraceIdentifier, _clock.UtcNow,
                resolution.TenantUserId, subject));
            await _sessions.SaveChangesAsync(ct);

            _cookies.Write(http, sessionToken, csrfToken);
            if (!http.Response.HasStarted)
                http.Response.Redirect(SafeReturn(returnTo));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Producer session establishment failed for {TenantUserId}.", resolution.TenantUserId);
            await DenyAsync(http, "session-write-failed", subject, ct);
        }
    }

    /// <summary>NotFound/Rejected → mints a single-use ticket (server <c>RegistrationTickets</c> row = the replay
    /// authority, REQ-3.4) and redirects to the SPA register page carrying the signed wire token (REQ-9.4/5.2). The
    /// wire token's TTL matches the row's expiry (both from <c>Producer:Registration:TicketTtlMinutes</c>).</summary>
    private async Task IssueTicketAndRedirectAsync(
        HttpContext http, string subject, string email, string? hostedDomain, TicketPurpose purpose, CancellationToken ct)
    {
        string wireTicket;
        try
        {
            var ttl = TimeSpan.FromMinutes(_registration.TicketTtlMinutes);
            var row = RegistrationTicket.Issue(subject, email, hostedDomain, purpose, _clock.UtcNow, ttl);
            _tickets.Add(row);
            await _ticketUnitOfWork.SaveChangesAsync(ct);
            wireTicket = _ticketProtector.Protect(new ProducerTicketPayload(row.Id, subject, email, hostedDomain, purpose));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue a {Purpose} ticket at callback.", purpose);
            await DenyAsync(http, "ticket-issue-failed", subject, ct);
            return;
        }

        if (!http.Response.HasStarted)
            http.Response.Redirect(QueryHelpers.AddQueryString(_oidc.RegisterUrl, "ticket", wireTicket));
    }

    /// <summary>PendingApproval → 403 "awaiting approval", no session (REQ-9.4/22.5). A known applicant in a normal
    /// lifecycle state, not a security failure — so no denied audit.</summary>
    private static async Task RespondAwaitingApprovalAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Response.HasStarted)
            return;
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync("Your registration is awaiting approval.", ct);
    }

    /// <summary>Records a denied/failed auth attempt (REQ-9.5/21.2) on a FRESH scope (clean context — a half-built
    /// session on the request context is never committed) and redirects to the SPA error page with a non-sensitive
    /// reason. Used by the OIDC failure events too.</summary>
    public async Task DenyAsync(HttpContext http, string reason, string? subject, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IProducerAuthAuditWriter>();
            audit.Append(ProducerAuthAudit.For(ProducerAuthEventType.AuthDenied, http.TraceIdentifier, _clock.UtcNow,
                subject: subject, reason: reason));
            await audit.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record denied-auth audit ({Reason}).", reason);
        }

        if (!http.Response.HasStarted)
            http.Response.Redirect(QueryHelpers.AddQueryString(_oidc.ErrorPath, "reason", reason));
    }

    private string SafeReturn(string? returnTo) =>
        ReturnUrlPolicy.Resolve(returnTo, _session.ReturnUrlAllowlist, _session.DefaultReturnPath);

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
