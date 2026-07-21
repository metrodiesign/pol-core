using BuildingBlocks.Application;
using Mediator;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Api.Merchants;

/// <summary>Resolves the merchant user at callback time (REQ-9.4) — a pure lookup behind the source-generated
/// mediator so the 4-way branch policy in <see cref="MerchantUserLoginService"/> can be unit-tested without it
/// (mirrors <c>IAdminCallbackResolver</c>). The callback NEVER self-provisions (REQ-9.6).</summary>
internal interface IUserCallbackResolver
{
    Task<LoginResult> ResolveAtCallbackAsync(string subject, CancellationToken cancellationToken);
}

internal sealed class UserCallbackResolver(IMediator mediator) : IUserCallbackResolver
{
    public Task<LoginResult> ResolveAtCallbackAsync(string subject, CancellationToken cancellationToken) =>
        mediator.Send(new ResolveLoginQuery(subject), cancellationToken).AsTask();
}

/// <summary>
/// Callback-time state branch for the merchant-user BFF (REQ-9.4). On a verified Google identity it resolves the
/// MerchantUser and branches FOUR ways: <b>Active</b> → start a server session (session + login-success audit in ONE
/// keyed pol_admin tx) + cookies + redirect to the allowlisted returnTo; <b>NotFound</b> → mint a stateless
/// Registration ticket (signed+time-limited wire token, no server row) + redirect to the SPA register page;
/// <b>Rejected</b> → mint a Correction ticket + redirect to register (REQ-5.2); <b>PendingApproval</b> → 403
/// "awaiting approval" with no session (REQ-22.5). A Suspended account (or any failure) gets no session and an error
/// redirect. Every denial writes a denied-auth audit on a FRESH scope so a half-built session can never be committed
/// by the audit save (REQ-9.5/21.2). No secret, token, code, raw session id, or ticket is ever logged (REQ-14.3).
/// </summary>
// ponytail: DUPLICATE-shaped of AdminLoginService (4-way branch + ticket mint, NO self-provision) — deliberate.
internal sealed class UserLoginService
{
    private readonly IUserCallbackResolver _resolver;
    private readonly ISessionStore _sessions;
    private readonly IAuthAuditWriter _audit;
    private readonly UserRegistrationTickets _ticketProtector;
    private readonly UserSessionCookies _cookies;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UserSessionOptions _session;
    private readonly UserOidcOptions _oidc;
    private readonly ILogger<UserLoginService> _logger;

    public UserLoginService(
        IUserCallbackResolver resolver,
        ISessionStore sessions,
        IAuthAuditWriter audit,
        UserRegistrationTickets ticketProtector,
        UserSessionCookies cookies,
        IClock clock,
        IServiceScopeFactory scopeFactory,
        IOptions<UserSessionOptions> session,
        IOptions<UserOidcOptions> oidc,
        ILogger<UserLoginService> logger)
    {
        _resolver = resolver;
        _sessions = sessions;
        _audit = audit;
        _ticketProtector = ticketProtector;
        _cookies = cookies;
        _clock = clock;
        _scopeFactory = scopeFactory;
        _session = session.Value;
        _oidc = oidc.Value;
        _logger = logger;
    }

    private SessionPolicy Policy => new(
        TimeSpan.FromMinutes(_session.IdleMinutes),
        TimeSpan.FromHours(_session.AbsoluteHours),
        TimeSpan.FromMinutes(_session.RotationMinutes),
        TimeSpan.FromSeconds(_session.GraceSeconds));

    /// <summary>Branches the callback on the merchant user's lifecycle state (REQ-9.4). Identity is the verified
    /// id_token's (REQ-9.3); the form/request can never override it.</summary>
    public async Task HandleCallbackAsync(
        HttpContext http, string? subject, string? email, string? hostedDomain, string provider, string? returnTo,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email))
        {
            await DenyAsync(http, "missing-identity", subject, ct);
            return;
        }

        LoginResult result;
        try
        {
            result = await _resolver.ResolveAtCallbackAsync(subject, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merchant user resolution failed at callback.");
            await DenyAsync(http, "resolve-failed", subject, ct);
            return;
        }

        switch (result.Outcome)
        {
            case LoginOutcome.Active:
                await EstablishSessionAsync(http, result.Resolution!, subject, returnTo, ct);
                break;
            case LoginOutcome.NotFound:
                await IssueTicketAndRedirectAsync(http, subject, email, hostedDomain, provider, TicketPurpose.Registration, ct);
                break;
            case LoginOutcome.Rejected:
                await IssueTicketAndRedirectAsync(http, subject, email, hostedDomain, provider, TicketPurpose.Correction, ct);
                break;
            case LoginOutcome.PendingApproval:
                RespondAwaitingApproval(http);
                break;
            case LoginOutcome.Suspended:
            default:
                await DenyAsync(http, "suspended", subject, ct);
                break;
        }
    }

    /// <summary>Active → opens a server session (REQ-10.1). Session + login-success audit commit TOGETHER on the
    /// request's keyed pol_admin context (no partial). Any failure after resolution → no partial session, a denied
    /// audit on a fresh scope + an error redirect, never a 500 (REQ-9.5).</summary>
    private async Task EstablishSessionAsync(
        HttpContext http, Resolution resolution, string subject, string? returnTo, CancellationToken ct)
    {
        try
        {
            var sessionToken = UserTokens.NewOpaqueToken();
            var csrfToken = UserTokens.NewOpaqueToken();
            var session = Session.Start(resolution.MerchantUserId, UserTokens.Hash(sessionToken),
                _clock.UtcNow, Policy,
                http.Connection.RemoteIpAddress?.ToString(),
                Truncate(http.Request.Headers.UserAgent.ToString(), 256));

            _sessions.Add(session);
            _audit.Append(AuthAudit.For(AuthEventType.LoginSuccess, http.TraceIdentifier, _clock.UtcNow,
                resolution.MerchantUserId, subject));
            await _sessions.SaveChangesAsync(ct);

            _cookies.Write(http, sessionToken, csrfToken);
            if (!http.Response.HasStarted)
                http.Response.Redirect(SafeReturn(returnTo));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merchant user session establishment failed for {MerchantUserId}.", resolution.MerchantUserId);
            await DenyAsync(http, "session-write-failed", subject, ct);
        }
    }

    /// <summary>NotFound/Rejected → mints a stateless single-use ticket (signed+time-limited wire token, no server
    /// row) and redirects to the SPA register page (REQ-9.4/5.2). The token is self-contained; replay/duplicate
    /// safety is the account's unique (Subject) index at submit time (REQ-4.6). Nothing is persisted here, so a
    /// repeated callback simply mints a fresh, harmless token.</summary>
    private async Task IssueTicketAndRedirectAsync(
        HttpContext http, string subject, string email, string? hostedDomain, string provider, TicketPurpose purpose,
        CancellationToken ct)
    {
        string wireTicket;
        try
        {
            wireTicket = _ticketProtector.Protect(new UserTicketPayload(subject, email, hostedDomain, purpose, provider));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue a {Purpose} ticket at callback.", purpose);
            await DenyAsync(http, "ticket-issue-failed", subject, ct);
            return;
        }

        if (!http.Response.HasStarted)
            http.Response.Redirect(QueryHelpers.AddQueryString(ToSpa(_oidc.RegisterUrl), "ticket", wireTicket));
    }

    /// <summary>PendingApproval → redirect to the SPA error page with <c>reason=awaiting-approval</c>, no session
    /// (REQ-9.4/22.5). A known applicant in a normal lifecycle state, not a security failure — so no denied audit.
    /// Uses the same redirect+reason contract as every other callback outcome (the browser navigation cannot consume
    /// a JSON/plain-text body); the FE renders awaiting-approval as info, not error, off the reason code.</summary>
    private void RespondAwaitingApproval(HttpContext http)
    {
        if (!http.Response.HasStarted)
            http.Response.Redirect(QueryHelpers.AddQueryString(ToSpa(_oidc.ErrorPath), "reason", "awaiting-approval"));
    }

    /// <summary>Records a denied/failed auth attempt (REQ-9.5/21.2) on a FRESH scope (clean context — a half-built
    /// session on the request context is never committed) and redirects to the SPA error page with a non-sensitive
    /// reason. Used by the OIDC failure events too.</summary>
    public async Task DenyAsync(HttpContext http, string reason, string? subject, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuthAuditWriter>();
            audit.Append(AuthAudit.For(AuthEventType.AuthDenied, http.TraceIdentifier, _clock.UtcNow,
                subject: subject, reason: reason));
            await audit.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record denied-auth audit ({Reason}).", reason);
        }

        if (!http.Response.HasStarted)
            http.Response.Redirect(QueryHelpers.AddQueryString(ToSpa(_oidc.ErrorPath), "reason", reason));
    }

    private string SafeReturn(string? returnTo) =>
        ToSpa(ReturnUrlPolicy.Resolve(returnTo, _session.ReturnUrlAllowlist, _session.DefaultReturnPath));

    /// <summary>The callback lands on the API origin, so a relative SPA path must be made absolute against the
    /// configured SPA origin (blank SpaBaseUrl or an already-absolute URL = unchanged). Mirrors admin LoginService.</summary>
    private string ToSpa(string path) =>
        path.StartsWith('/') && !string.IsNullOrEmpty(_session.SpaBaseUrl)
            ? _session.SpaBaseUrl.TrimEnd('/') + path
            : path;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
