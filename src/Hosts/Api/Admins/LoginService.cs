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
    /// <param name="employeeId">Historical non-Microsoft seam; Admin Microsoft callbacks use the typed method below.</param>
    Task<ResolveResult> ResolveAtCallbackAsync(
        ProviderIdentity identity, string? employeeId, string correlationId, CancellationToken cancellationToken);

    Task<ResolveResult> ResolveMicrosoftAtCallbackAsync(
        Guid tenantId,
        Guid objectId,
        string? email,
        string? employeeId,
        string correlationId,
        CancellationToken cancellationToken);
}

internal sealed class CallbackResolver : ICallbackResolver
{
    private readonly IMediator _mediator;

    public CallbackResolver(IMediator mediator) => _mediator = mediator;

    public async Task<ResolveResult> ResolveAtCallbackAsync(
        ProviderIdentity identity, string? employeeId, string correlationId, CancellationToken cancellationToken)
    {
        if (string.Equals(identity.Provider, User.MicrosoftProvider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Microsoft callbacks require the tenant-aware resolver seam.");
        return await _mediator.Send(new ResolveQuery(identity), cancellationToken);
    }

    public async Task<ResolveResult> ResolveMicrosoftAtCallbackAsync(
        Guid tenantId,
        Guid objectId,
        string? email,
        string? employeeId,
        string correlationId,
        CancellationToken cancellationToken) =>
        await _mediator.Send(
            new ResolveMicrosoftAdminCommand(tenantId, objectId, email, employeeId, correlationId), cancellationToken);
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

    /// <summary>Establishes a historical non-Microsoft provider session.</summary>
    public async Task EstablishSessionAsync(
        HttpContext http, string provider, string? subject, string? employeeId, string? returnTo, CancellationToken ct)
    {
        if (string.Equals(provider, User.MicrosoftProvider, StringComparison.OrdinalIgnoreCase))
        {
            await DenyAsync(http, "workforce-access-denied", null, ct);
            return;
        }
        if (string.IsNullOrEmpty(subject))
        {
            await DenyAsync(http, "missing-subject", null, ct);
            return;
        }

        var identity = new ProviderIdentity(provider, subject);
        await EstablishResolvedSessionAsync(
            http,
            auditSubject: subject,
            returnTo,
            (correlationId, token) => _resolver.ResolveAtCallbackAsync(
                identity, employeeId, correlationId, token),
            ct);
    }

    /// <summary>Establishes a Microsoft session from the already validated tenant-aware callback record.</summary>
    public Task EstablishMicrosoftSessionAsync(
        HttpContext http,
        MicrosoftWorkforceClaims claims,
        string? returnTo,
        CancellationToken cancellationToken) =>
        EstablishResolvedSessionAsync(
            http,
            auditSubject: null,
            returnTo,
            (correlationId, token) => _resolver.ResolveMicrosoftAtCallbackAsync(
                claims.TenantId, claims.ObjectId, claims.Email, claims.EmployeeId, correlationId, token),
            cancellationToken);

    private async Task EstablishResolvedSessionAsync(
        HttpContext http,
        string? auditSubject,
        string? returnTo,
        Func<string, CancellationToken, Task<ResolveResult>> resolve,
        CancellationToken cancellationToken)
    {
        var correlationId = http.TraceIdentifier;
        ResolveResult result;
        try
        {
            result = await resolve(correlationId, cancellationToken);
        }
        catch (Exception)
        {
            _logger.LogError("Admin resolution failed at callback. CorrelationId {CorrelationId}.", correlationId);
            await DenyAsync(http, "resolve-failed", auditSubject, cancellationToken);
            return;
        }

        if (result.Outcome != ResolveOutcome.Resolved)
        {
            var reason = result.Outcome switch
            {
                ResolveOutcome.Resolved => throw new System.Diagnostics.UnreachableException(),
                ResolveOutcome.NotFound => "not-provisioned",
                ResolveOutcome.Suspended => "suspended",
                ResolveOutcome.IdentityConflict => "identity-conflict",
                ResolveOutcome.EmployeeProfileMissing => EmployeeProfileException.Missing,
                ResolveOutcome.EmployeeProfileInvalid => EmployeeProfileException.Invalid,
                ResolveOutcome.EmployeeProfileUnavailable => EmployeeProfileException.Unavailable,
                _ => throw new System.Diagnostics.UnreachableException("Unmapped admin resolve outcome."),
            };
            await DenyAsync(
                http, reason, auditSubject, cancellationToken, auditReason: result.DenialReason);
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

            _sessions.Add(session);
            _audit.Append(AuthAudit.For(
                AuthEventType.LoginSuccess, correlationId, _clock.UtcNow, resolution.AdminId, auditSubject));
            await _sessions.SaveChangesAsync(cancellationToken);

            _cookies.Write(http, sessionToken, csrfToken);
            http.Response.Redirect(SafeReturn(returnTo));
        }
        catch (Exception)
        {
            _logger.LogError(
                "Admin session establishment failed for {AdminId}. CorrelationId {CorrelationId}.",
                resolution.AdminId, correlationId);
            await DenyAsync(http, "session-write-failed", auditSubject, cancellationToken);
        }
    }

    /// <summary>Records a denied/failed auth attempt (REQ-2.8/12.4) on a FRESH scope (clean context) and
    /// redirects to the SPA error page with a non-sensitive reason. Used by the OIDC failure events too.</summary>
    public async Task DenyAsync(
        HttpContext http, string reason, string? subject, CancellationToken ct, string? auditReason = null)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<IAuthAuditWriter>();
            audit.Append(AuthAudit.For(
                AuthEventType.AuthDenied, http.TraceIdentifier, _clock.UtcNow, subject: subject, reason: auditReason ?? reason));
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
