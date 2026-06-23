using System.Security.Claims;
using System.Text.Encodings.Web;
using Admin.Application;
using Admin.Application.ResolveAdmin;
using Admin.Domain;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Api;

/// <summary>
/// Authenticates every <c>/admin/*</c> request via the opaque <c>__Host-adm_session</c> cookie (REQ-4/5/9). It
/// looks the session up by its SHA-256 hash, runs the decision table (REQ-5), re-resolves the admin's current
/// Status/Tier/accessible set READ-ONLY (REQ-9), builds a principal carrying the <c>admin_tier</c> + <c>sub</c>
/// claims the existing endpoints read, binds <see cref="IAdminScope"/>, and transparently rotates the cookie
/// past the rotation age (REQ-5.1). A Google id_token Bearer is never consulted on these routes (REQ-4.4): no
/// cookie -&gt; NoResult, and the <c>admin</c> policy is pinned to this scheme only.
/// </summary>
internal sealed class AdminSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "AdminSession";

    private readonly IAdminSessionStore _sessions;
    private readonly IAdminAuthAuditWriter _audit;
    private readonly AdminSessionCookies _cookies;
    private readonly IAdminSessionResolver _resolver;
    private readonly AdminScope _scope;
    private readonly IClock _clock;
    private readonly AdminSessionOptions _options;

    public AdminSessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAdminSessionStore sessions,
        IAdminAuthAuditWriter audit,
        AdminSessionCookies cookies,
        IAdminSessionResolver resolver,
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

    private AdminSessionPolicy Policy => new(
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
        var session = await _sessions.FindByTokenHashAsync(AdminSessionTokens.Hash(token), ct);
        if (session is null)
            return AuthenticateResult.Fail("Unknown session.");

        var now = _clock.UtcNow;
        var policy = Policy;

        var familyActiveId = session.Status == AdminSessionStatus.Superseded
            ? await _sessions.GetFamilyActiveSessionIdAsync(session.FamilyId, ct)
            : null;

        switch (AdminSessionDecisionPolicy.Decide(session, familyActiveId, now, policy))
        {
            case AdminSessionDecision.Reject:
                return AuthenticateResult.Fail("Session is revoked or expired."); // 401 (REQ-4.2/5.4)

            case AdminSessionDecision.ReuseRevokeFamily:
                await _sessions.RevokeFamilyAsync(session.FamilyId, ct);
                _audit.Append(AdminAuthAudit.For(AdminAuthEventType.FamilyRevokedReuse, Context.TraceIdentifier, now,
                    session.AdminAccountId, reason: "reuse-detected"));
                await _audit.SaveChangesAsync(ct);
                return AuthenticateResult.Fail("Session reuse detected."); // 401, family killed (REQ-5.3/12.1)

            case AdminSessionDecision.ServeActive:
            case AdminSessionDecision.ServeUnderGrace:
            default:
                break;
        }

        // Per-request READ-ONLY resolution (REQ-9.1/9.4): fresh Status/Tier/accessible + Subject by account id.
        var resolved = await _resolver.ResolveByIdAsync(session.AdminAccountId, ct);
        if (resolved.Outcome != AdminResolveOutcome.Resolved || resolved.Resolution is null)
            return AuthenticateResult.Fail("Admin is suspended or no longer exists."); // suspend -> next request 401 (REQ-6.3/9.2)

        var resolution = resolved.Resolution;
        _scope.Set(resolution); // bind IAdminScope so endpoints read scope.Current

        // The principal must carry the claims existing consumers read: admin_tier (the Super-only tier gate) and
        // sub (the provisioning actor id on POST /admin/tenants). (REQ-4.3 / P1-2)
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("admin_tier", resolution.Tier.ToString()));
        if (!string.IsNullOrEmpty(resolved.Subject))
            identity.AddClaim(new Claim("sub", resolved.Subject));
        identity.AddClaim(new Claim("email", resolution.Email));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resolution.AdminId.ToString()));
        var principal = new ClaimsPrincipal(identity);

        // Rotation + idle-slide apply only to a live Active session (a grace predecessor is already superseded).
        if (session.Status == AdminSessionStatus.Active)
        {
            if (now - session.IssuedAt >= policy.Rotation)
                await TryRotateAsync(session, now, policy, ct);
            else
                await MaybeSlideIdleAsync(session, now, policy, ct);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private async Task TryRotateAsync(AdminSession session, DateTime now, AdminSessionPolicy policy, CancellationToken ct)
    {
        var newToken = AdminSessionTokens.NewOpaqueToken();
        var csrfToken = AdminSessionTokens.NewOpaqueToken();
        var successor = session.Rotate(AdminSessionTokens.Hash(newToken), now, policy);

        // Atomic single-winner supersede (REQ-5.5): if a concurrent request already rotated this session, we lose
        // and serve under grace with the existing cookie (no Set-Cookie) — exactly one successor is created.
        if (!await _sessions.TrySupersedeAsync(session.Id, successor.Id, now, ct))
            return;

        _sessions.Add(successor);
        _audit.Append(AdminAuthAudit.For(AdminAuthEventType.Rotated, Context.TraceIdentifier, now, session.AdminAccountId));
        await _sessions.SaveChangesAsync(ct);
        _cookies.Write(Context, newToken, csrfToken); // safe: UseAuthentication runs before the response body
    }

    private async Task MaybeSlideIdleAsync(AdminSession session, DateTime now, AdminSessionPolicy policy, CancellationToken ct)
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
internal interface IAdminSessionResolver
{
    Task<AdminByIdResult> ResolveByIdAsync(Guid adminAccountId, CancellationToken cancellationToken);
}

internal sealed class AdminSessionResolver(IMediator mediator) : IAdminSessionResolver
{
    public Task<AdminByIdResult> ResolveByIdAsync(Guid adminAccountId, CancellationToken cancellationToken) =>
        mediator.Send(new ResolveAdminByIdQuery(adminAccountId), cancellationToken).AsTask();
}

internal static class AdminSessionSchemeRegistration
{
    /// <summary>Registers the AdminSession cookie scheme and REDEFINES the <c>admin</c> authorization policy to
    /// pin that scheme and require only an authenticated user (REQ-10.6) — the old
    /// <c>RequireClaim("role","admin")</c> is dropped (a session principal has no role claim). Existing
    /// <c>.RequireAuthorization("admin")</c> call sites are unchanged.</summary>
    public static IServiceCollection AddAdminSessionScheme(this IServiceCollection services)
    {
        services.AddScoped<IAdminSessionResolver, AdminSessionResolver>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AdminSessionAuthenticationHandler>(
                AdminSessionAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy("admin", policy => policy
                .AddAuthenticationSchemes(AdminSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }
}
