using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SharedKernel;

namespace Api.Admins;

/// <summary>Resolves the admin at callback time (REQ-2.5): an existing admin is a READ; an eligible Microsoft
/// workforce identity is JIT-provisioned as a least-privilege Scoped account. The IMediator seam is behind this
/// interface so the session-establishment policy can be tested without the source-generated mediator.</summary>
internal interface ICallbackResolver
{
    Task<ResolveResult> ResolveAtCallbackAsync(
        ProviderIdentity identity, string correlationId, CancellationToken cancellationToken);
}

internal sealed class CallbackResolver : ICallbackResolver
{
    private readonly IMediator _mediator;

    public CallbackResolver(IMediator mediator) => _mediator = mediator;

    public async Task<ResolveResult> ResolveAtCallbackAsync(
        ProviderIdentity identity, string correlationId, CancellationToken cancellationToken)
    {
        if (string.Equals(identity.Provider, User.MicrosoftProvider, StringComparison.Ordinal))
            return await _mediator.Send(
                new ResolveMicrosoftAdminCommand(identity.Subject, correlationId), cancellationToken);

        var result = await _mediator.Send(new ResolveQuery(identity), cancellationToken);
        return result;
    }
}

/// <summary>
/// Callback-time session establishment for the BFF (REQ-2/3/12). A success creates a server session + login
/// audit committed TOGETHER on the request's keyed pol_admin context, then sets cookies and redirects to the
/// allowlisted returnTo. Every denial/failure writes a denied-auth audit on a FRESH scope (so a half-built
/// session can never be committed by the audit save — REQ-2.7) and redirects to the SPA error page with a
/// non-sensitive reason. No secret, token, code, or raw session id is ever logged (REQ-8.3).
/// </summary>
internal sealed class LoginService
{
    private readonly ICallbackResolver _resolver;
    private readonly ISessionStore _sessions;
    private readonly IAuthAuditWriter _audit;
    private readonly SessionCookies _cookies;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AdminSessionOptions _session;
    private readonly AdminAuthOptions _oidc;
    private readonly ILogger<LoginService> _logger;

    public LoginService(
        ICallbackResolver resolver,
        ISessionStore sessions,
        IAuthAuditWriter audit,
        SessionCookies cookies,
        IClock clock,
        IServiceScopeFactory scopeFactory,
        IOptions<AdminSessionOptions> session,
        IOptions<AdminAuthOptions> oidc,
        ILogger<LoginService> logger)
    {
        _resolver = resolver;
        _sessions = sessions;
        _audit = audit;
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

    /// <summary>Establishes a session for a verified provider identity, or denies (REQ-2.5/2.6/2.7/3.1/12.1).
    /// <paramref name="provider"/> = the lowercase provider slug ("google"/"microsoft") the identity came from.</summary>
    public async Task EstablishSessionAsync(
        HttpContext http, string provider, string? subject, string? returnTo, CancellationToken ct)
    {
        var auditSubject = provider == User.MicrosoftProvider ? null : subject;
        if (string.IsNullOrEmpty(subject))
        {
            await DenyAsync(http, "missing-subject", null, ct);
            return;
        }

        var correlationId = http.TraceIdentifier;
        ResolveResult result;
        try
        {
            result = await _resolver.ResolveAtCallbackAsync(
                new ProviderIdentity(provider, subject), correlationId, ct);
        }
        catch (Exception)
        {
            _logger.LogError("Admin resolution failed at callback. CorrelationId {CorrelationId}.", correlationId);
            await DenyAsync(http, "resolve-failed", auditSubject, ct);
            return;
        }

        if (result.Outcome != ResolveOutcome.Resolved)
        {
            var reason = result.Outcome switch
            {
                ResolveOutcome.Suspended => "suspended",
                ResolveOutcome.IdentityConflict => "identity-conflict",
                _ => "not-provisioned",
            };
            await DenyAsync(http, reason, auditSubject, ct);
            return;
        }

        var resolution = result.Resolution!;
        try
        {
            var sessionToken = SessionTokens.NewOpaqueToken();
            var csrfToken = SessionTokens.NewOpaqueToken();
            var session = Session.Start(resolution.AdminId, SessionTokens.Hash(sessionToken), _clock.UtcNow, Policy,
                http.Connection.RemoteIpAddress?.ToString(),
                Truncate(http.Request.Headers.UserAgent.ToString(), 256));

            // session + login-success audit commit TOGETHER on the request's keyed pol_admin context (no partial).
            _sessions.Add(session);
            _audit.Append(AuthAudit.For(AuthEventType.LoginSuccess, correlationId, _clock.UtcNow, resolution.AdminId, auditSubject));
            await _sessions.SaveChangesAsync(ct);

            _cookies.Write(http, sessionToken, csrfToken);
            http.Response.Redirect(SafeReturn(returnTo));
        }
        catch (Exception)
        {
            // REQ-2.7: any failure after resolution -> no partial session (the half-built session on THIS context
            // is never committed; the deny audit runs on a fresh scope), denied audit + error redirect, not 500.
            _logger.LogError(
                "Admin session establishment failed for {AdminId}. CorrelationId {CorrelationId}.",
                resolution.AdminId, correlationId);
            await DenyAsync(http, "session-write-failed", auditSubject, ct);
        }
    }

    /// <summary>Records a denied/failed auth attempt (REQ-2.8/12.4) on a FRESH scope (clean context) and
    /// redirects to the SPA error page with a non-sensitive reason. Used by the OIDC failure events too.</summary>
    public async Task DenyAsync(HttpContext http, string reason, string? subject, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuthAuditWriter>();
            audit.Append(AuthAudit.For(AuthEventType.AuthDenied, http.TraceIdentifier, _clock.UtcNow, subject: subject, reason: reason));
            await audit.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            _logger.LogError(
                "Failed to record denied-auth audit. Reason {Reason}. CorrelationId {CorrelationId}.",
                reason, http.TraceIdentifier);
        }

        if (!http.Response.HasStarted)
            http.Response.Redirect(QueryHelpers.AddQueryString(ToSpa(_oidc.ErrorPath), "reason", reason));
    }

    private string SafeReturn(string? returnTo) =>
        ToSpa(ReturnUrlPolicy.Resolve(returnTo, _session.ReturnUrlAllowlist, _session.DefaultReturnPath));

    /// <summary>The callback lands on the API origin, so relative SPA paths use WebAppBaseUrl while the exact
    /// Development-only Scalar path uses ScalarBaseUrl when configured.</summary>
    private string ToSpa(string path) =>
        path == "/scalar" && !string.IsNullOrEmpty(_session.ScalarBaseUrl)
            ? _session.ScalarBaseUrl.TrimEnd('/') + path
            : path.StartsWith('/') && !string.IsNullOrEmpty(_session.WebAppBaseUrl)
            ? _session.WebAppBaseUrl.TrimEnd('/') + path
            : path;

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];
}
