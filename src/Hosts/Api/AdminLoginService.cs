using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Api;

/// <summary>Same-origin return-path allowlist (open-redirect prevention, REQ-1.3). A requested target is honored
/// ONLY when it is a relative same-origin path (single leading slash) that is in the configured allowlist; any
/// other value falls back to the default landing path.</summary>
internal static class ReturnUrlPolicy
{
    public static string Resolve(string? requested, IReadOnlyCollection<string> allowlist, string defaultPath) =>
        !string.IsNullOrEmpty(requested)
        && requested.StartsWith('/')
        && !requested.StartsWith("//", StringComparison.Ordinal)   // protocol-relative -> off-origin
        && allowlist.Contains(requested, StringComparer.Ordinal)
            ? requested
            : defaultPath;
}

/// <summary>Resolves the admin at callback time (REQ-2.5): an existing admin is a READ; first login binds an
/// invited Scoped account by email, else allowlist self-provisions a Super (idempotent). The IMediator seam is
/// behind this interface so the session-establishment policy can be tested without the source-generated mediator.</summary>
internal interface IAdminCallbackResolver
{
    Task<ResolveResult> ResolveAtCallbackAsync(string subject, string email, string correlationId, CancellationToken cancellationToken);
}

internal sealed class AdminCallbackResolver : IAdminCallbackResolver
{
    private readonly IMediator _mediator;
    private readonly IConfiguration _configuration;

    public AdminCallbackResolver(IMediator mediator, IConfiguration configuration)
    {
        _mediator = mediator;
        _configuration = configuration;
    }

    public async Task<ResolveResult> ResolveAtCallbackAsync(string subject, string email, string correlationId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ResolveQuery(subject), cancellationToken);
        if (result.Outcome != ResolveOutcome.NotFound)
            return result;

        // Invite-bind FIRST so an invited email never collides with the unique Email index via self-provision.
        if (!string.IsNullOrEmpty(email))
        {
            var bound = await _mediator.Send(new BindInvitedCommand(subject, email, correlationId), cancellationToken);
            if (bound.Outcome != ResolveOutcome.NotFound)
                return bound;
        }

        var allowlist = _configuration.GetSection("AdminAllowlist:Subjects").Get<string[]>() ?? [];
        if (allowlist.Contains(subject, StringComparer.Ordinal))
            return ResolveResult.Of(await _mediator.Send(new SelfProvisionSuperCommand(subject, email, correlationId), cancellationToken));

        return ResolveResult.NotFound;
    }
}

/// <summary>
/// Callback-time session establishment for the BFF (REQ-2/3/12). A success creates a server session + login
/// audit committed TOGETHER on the request's keyed pol_admin context, then sets cookies and redirects to the
/// allowlisted returnTo. Every denial/failure writes a denied-auth audit on a FRESH scope (so a half-built
/// session can never be committed by the audit save — REQ-2.7) and redirects to the SPA error page with a
/// non-sensitive reason. No secret, token, code, or raw session id is ever logged (REQ-8.3).
/// </summary>
internal sealed class AdminLoginService
{
    private readonly IAdminCallbackResolver _resolver;
    private readonly ISessionStore _sessions;
    private readonly IAuthAuditWriter _audit;
    private readonly PlatformUserSessionCookies _cookies;
    private readonly IClock _clock;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PlatformUserSessionOptions _session;
    private readonly AdminOidcOptions _oidc;
    private readonly ILogger<AdminLoginService> _logger;

    public AdminLoginService(
        IAdminCallbackResolver resolver,
        ISessionStore sessions,
        IAuthAuditWriter audit,
        PlatformUserSessionCookies cookies,
        IClock clock,
        IServiceScopeFactory scopeFactory,
        IOptions<PlatformUserSessionOptions> session,
        IOptions<AdminOidcOptions> oidc,
        ILogger<AdminLoginService> logger)
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

    /// <summary>Establishes a session for a verified Google identity, or denies (REQ-2.5/2.6/2.7/3.1/12.1).</summary>
    public async Task EstablishSessionAsync(HttpContext http, string? subject, string? email, string? returnTo, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subject))
        {
            await DenyAsync(http, "missing-subject", null, ct);
            return;
        }

        var correlationId = http.TraceIdentifier;
        ResolveResult result;
        try
        {
            result = await _resolver.ResolveAtCallbackAsync(subject, email ?? string.Empty, correlationId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin resolution failed at callback.");
            await DenyAsync(http, "resolve-failed", subject, ct);
            return;
        }

        if (result.Outcome != ResolveOutcome.Resolved)
        {
            await DenyAsync(http, result.Outcome == ResolveOutcome.Suspended ? "suspended" : "not-provisioned", subject, ct);
            return;
        }

        var resolution = result.Resolution!;
        try
        {
            var sessionToken = PlatformUserSessionTokens.NewOpaqueToken();
            var csrfToken = PlatformUserSessionTokens.NewOpaqueToken();
            var session = Session.Start(resolution.AdminId, PlatformUserSessionTokens.Hash(sessionToken), _clock.UtcNow, Policy,
                http.Connection.RemoteIpAddress?.ToString(),
                Truncate(http.Request.Headers.UserAgent.ToString(), 256));

            // session + login-success audit commit TOGETHER on the request's keyed pol_admin context (no partial).
            _sessions.Add(session);
            _audit.Append(AuthAudit.For(AuthEventType.LoginSuccess, correlationId, _clock.UtcNow, resolution.AdminId, subject));
            await _sessions.SaveChangesAsync(ct);

            _cookies.Write(http, sessionToken, csrfToken);
            http.Response.Redirect(SafeReturn(returnTo));
        }
        catch (Exception ex)
        {
            // REQ-2.7: any failure after resolution -> no partial session (the half-built session on THIS context
            // is never committed; the deny audit runs on a fresh scope), denied audit + error redirect, not 500.
            _logger.LogError(ex, "Admin session establishment failed for {AdminId}.", resolution.AdminId);
            await DenyAsync(http, "session-write-failed", subject, ct);
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
