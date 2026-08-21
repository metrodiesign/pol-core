using System.Security.Claims;
using System.Text.Encodings.Web;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Api.Admins;

/// <summary>
/// Authenticates every <c>/admin/*</c> request via the opaque <c>__Host-adm_session</c> cookie (REQ-4/5/9). It
/// looks the session up by its SHA-256 hash, runs the decision table (REQ-5), re-resolves the admin's current
/// Status/Tier/accessible set READ-ONLY (REQ-9), builds a principal carrying internal admin identity and tier,
/// binds <see cref="IAdminScope"/>, and transparently rotates the cookie
/// past the rotation age (REQ-5.1). A Google id_token Bearer is never consulted on these routes (REQ-4.4): no
/// cookie -&gt; NoResult, and the <c>admin</c> policy is pinned to this scheme only.
/// </summary>
internal sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AdminSession";

    private readonly ISessionStore _sessions;
    private readonly IAuthAuditWriter _audit;
    private readonly SessionCookies _cookies;
    private readonly ISessionResolver _resolver;
    private readonly AdminScope _scope;
    private readonly IClock _clock;
    private readonly AdminSessionOptions _options;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionStore sessions,
        IAuthAuditWriter audit,
        SessionCookies cookies,
        ISessionResolver resolver,
        AdminScope scope,
        IClock clock,
        IOptions<AdminSessionOptions> sessionOptions)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _audit = audit;
        _cookies = cookies;
        _resolver = resolver;
        _scope = scope;
        _clock = clock;
        _options = sessionOptions.Value;
    }

    private SessionPolicy Policy => new(
        TimeSpan.FromMinutes(_options.IdleMinutes),
        TimeSpan.FromHours(_options.AbsoluteHours),
        TimeSpan.FromMinutes(_options.RotationMinutes),
        TimeSpan.FromSeconds(_options.GraceSeconds));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = _cookies.ReadSessionToken(Context);
        if (token is null)
            return AuthenticateResult.NoResult(); // no cookie -> unauthenticated; no fallthrough to bearer (REQ-4.2/4.4)

        var ct = Context.RequestAborted;
        var session = await _sessions.FindByTokenHashAsync(SessionTokens.Hash(token), ct);
        if (session is null)
            return AuthenticateResult.Fail("Unknown session.");

        var now = _clock.UtcNow;
        var policy = Policy;

        var familyActiveId = session.Status == SessionStatus.Superseded
            ? await _sessions.GetFamilyActiveSessionIdAsync(session.FamilyId, ct)
            : null;

        switch (SessionDecisionPolicy.Decide(session, familyActiveId, now, policy))
        {
            case SessionDecision.Reject:
                return AuthenticateResult.Fail("Session is revoked or expired."); // 401 (REQ-4.2/5.4)

            case SessionDecision.ReuseRevokeFamily:
                await _sessions.RevokeFamilyAsync(session.FamilyId, ct);
                _audit.Append(AuthAudit.For(AuthEventType.FamilyRevokedReuse, Context.TraceIdentifier, now,
                    session.AdminUserId, reason: "reuse-detected"));
                await _audit.SaveChangesAsync(ct);
                return AuthenticateResult.Fail("Session reuse detected."); // 401, family killed (REQ-5.3/12.1)

            case SessionDecision.ServeActive:
            case SessionDecision.ServeUnderGrace:
            default:
                break;
        }

        // Per-request READ-ONLY resolution (REQ-9.1/9.4): fresh Status/Tier/accessible by account id.
        var resolved = await _resolver.ResolveByIdAsync(session.AdminUserId, ct);
        if (resolved.Outcome != ResolveOutcome.Resolved || resolved.Resolution is null)
            return AuthenticateResult.Fail("Admin is suspended or no longer exists."); // suspend -> next request 401 (REQ-6.3/9.2)

        var resolution = resolved.Resolution;
        _scope.Set(resolution); // bind IAdminScope so endpoints read scope.Current

        // External provider subjects stay out of the session principal. Internal account id is the actor identity.
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("admin_tier", resolution.Tier.ToString()));
        identity.AddClaim(new Claim("email", resolution.Email));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resolution.AdminId.ToString("D")));
        var principal = new ClaimsPrincipal(identity);

        // Rotation + idle-slide apply only to a live Active session (a grace predecessor is already superseded).
        if (session.Status == SessionStatus.Active)
        {
            if (now - session.IssuedAt >= policy.Rotation)
                await TryRotateAsync(session, now, policy, ct);
            else
                await MaybeSlideIdleAsync(session, now, policy, ct);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Admin session is required",
        };
        details.Extensions["code"] = "admin_session_required";
        details.Extensions["traceId"] = Context.TraceIdentifier;
        return Context.Response.WriteAsJsonAsync(
            details, options: null, contentType: "application/problem+json", cancellationToken: Context.RequestAborted);
    }

    private async Task TryRotateAsync(Session session, DateTime now, SessionPolicy policy, CancellationToken ct)
    {
        var newToken = SessionTokens.NewOpaqueToken();
        var csrfToken = SessionTokens.NewOpaqueToken();
        var successor = session.Rotate(SessionTokens.Hash(newToken), now, policy);

        // Atomic single-winner supersede (REQ-5.5): if a concurrent request already rotated this session, we lose
        // and serve under grace with the existing cookie (no Set-Cookie) — exactly one successor is created.
        if (!await _sessions.TrySupersedeAsync(session.Id, successor.Id, now, ct))
            return;

        _sessions.Add(successor);
        _audit.Append(AuthAudit.For(AuthEventType.Rotated, Context.TraceIdentifier, now, session.AdminUserId));
        await _sessions.SaveChangesAsync(ct);
        _cookies.Write(Context, newToken, csrfToken); // safe: UseAuthentication runs before the response body
    }

    private async Task MaybeSlideIdleAsync(Session session, DateTime now, SessionPolicy policy, CancellationToken ct)
    {
        // Lazy: persist at most ~once a minute (REQ-3.5 / Tech #8), bounded by the absolute expiry.
        var lastSlide = session.IdleExpiresAt - policy.Idle;
        if (now - lastSlide < TimeSpan.FromMinutes(1))
            return;
        var newIdle = now + policy.Idle;
        if (newIdle > session.AbsoluteExpiresAt)
            newIdle = session.AbsoluteExpiresAt;
        await _sessions.SlideIdleAsync(session.Id, newIdle, ct);
    }
}

/// <summary>READ-ONLY per-request admin resolution behind the source-generated mediator, so the auth handler's
/// decision/principal/rotation logic can be unit-tested without it (mirrors <see cref="IAdminCallbackResolver"/>).</summary>
internal interface ISessionResolver
{
    Task<ByIdResult> ResolveByIdAsync(Guid adminAccountId, CancellationToken cancellationToken);
}

internal sealed class SessionResolver(IMediator mediator) : ISessionResolver
{
    public Task<ByIdResult> ResolveByIdAsync(Guid adminAccountId, CancellationToken cancellationToken) =>
        mediator.Send(new ResolveByIdQuery(adminAccountId), cancellationToken).AsTask();
}

internal static class SessionSchemeRegistration
{
    /// <summary>Registers the Session cookie scheme and REDEFINES the <c>admin</c> authorization policy to
    /// pin that scheme and require only an authenticated user (REQ-10.6) — the old
    /// <c>RequireClaim("role","admin")</c> is dropped (a session principal has no role claim). Existing
    /// <c>.RequireAuthorization("admin")</c> call sites are unchanged.</summary>
    public static IServiceCollection AddPlatformUserSessionScheme(this IServiceCollection services)
    {
        services.AddScoped<ISessionResolver, SessionResolver>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(SessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }
}
