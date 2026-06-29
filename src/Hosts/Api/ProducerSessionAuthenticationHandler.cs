using System.Security.Claims;
using System.Text.Encodings.Web;
using BuildingBlocks.Application;
using Mediator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Producer.Application;
using Producer.Domain;

namespace Api;

/// <summary>
/// Authenticates every producer request via the opaque <c>__Host-prd_session</c> cookie (REQ-11/12/17). It looks the
/// session up by its SHA-256 hash, runs the decision table (REQ-11), re-resolves the producer's current
/// Status/Tenant/effective permissions READ-ONLY (REQ-12.4/17.1), builds a principal carrying the <c>tenant_id</c>
/// claim the existing <see cref="HttpTenantContext"/> path reads (S4 — NOT <c>ITenantScope.Begin</c>), binds
/// <see cref="IProducerScope"/> for <c>RequireProducerPermission</c>, and transparently rotates the cookie past the
/// rotation age (REQ-11.1). No cookie -&gt; NoResult so the dual-scheme <c>producer</c> policy can fall through to the
/// tenant Bearer scheme (REQ-17.3); a session exists only for an Active producer (REQ-10.1), so a suspend/reject
/// denies the next request (REQ-12.4).
/// </summary>
// ponytail: DUPLICATE of Api.AdminSessionAuthenticationHandler (AdminAccountId -> TenantUserId; admin_tier claim ->
// tenant_id claim; no fall-through to bearer becomes NoResult so the dual-scheme policy can) — deliberate debt.
internal sealed class ProducerSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ProducerSession";

    private readonly IProducerSessionStore _sessions;
    private readonly IProducerAuthAuditWriter _audit;
    private readonly ProducerSessionCookies _cookies;
    private readonly IProducerSessionResolver _resolver;
    private readonly ProducerScope _scope;
    private readonly IClock _clock;
    private readonly ProducerSessionOptions _options;

    public ProducerSessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IProducerSessionStore sessions,
        IProducerAuthAuditWriter audit,
        ProducerSessionCookies cookies,
        IProducerSessionResolver resolver,
        ProducerScope scope,
        IClock clock,
        IOptions<ProducerSessionOptions> sessionOptions)
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

    private ProducerSessionPolicy Policy => new(
        TimeSpan.FromMinutes(_options.IdleMinutes),
        TimeSpan.FromHours(_options.AbsoluteHours),
        TimeSpan.FromMinutes(_options.RotationMinutes),
        TimeSpan.FromSeconds(_options.GraceSeconds));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = _cookies.ReadSessionToken(Context);
        if (token is null)
            return AuthenticateResult.NoResult(); // no cookie -> let the dual-scheme policy try Bearer (REQ-17.3)

        var ct = Context.RequestAborted;
        var session = await _sessions.FindByTokenHashAsync(ProducerTokens.Hash(token), ct);
        if (session is null)
            return AuthenticateResult.Fail("Unknown session.");

        var now = _clock.UtcNow;
        var policy = Policy;

        var familyActiveId = session.Status == ProducerSessionStatus.Superseded
            ? await _sessions.GetFamilyActiveSessionIdAsync(session.FamilyId, ct)
            : null;

        switch (ProducerSessionDecisionPolicy.Decide(session, familyActiveId, now, policy))
        {
            case ProducerSessionDecision.Reject:
                return AuthenticateResult.Fail("Session is revoked or expired."); // 401 (REQ-11.3/11.4)

            case ProducerSessionDecision.ReuseRevokeFamily:
                await _sessions.RevokeFamilyAsync(session.FamilyId, ct);
                _audit.Append(ProducerAuthAudit.For(ProducerAuthEventType.FamilyRevokedReuse, Context.TraceIdentifier, now,
                    session.ProducerAccountId, reason: "reuse-detected"));
                await _audit.SaveChangesAsync(ct);
                return AuthenticateResult.Fail("Session reuse detected."); // 401, family killed (REQ-11.3)

            case ProducerSessionDecision.ServeActive:
            case ProducerSessionDecision.ServeUnderGrace:
            default:
                break;
        }

        // Per-request READ-ONLY resolution (REQ-12.4/17.1): a suspend/reject/role-change takes effect within one request.
        var resolved = await _resolver.ResolveByIdAsync(session.ProducerAccountId, ct);
        if (resolved.Outcome != ProducerByIdOutcome.Resolved || resolved.Resolution is null)
            return AuthenticateResult.Fail("Producer is not active or no longer exists."); // suspend -> next request 401

        var resolution = resolved.Resolution;
        _scope.Set(resolution); // bind IProducerScope so RequireProducerPermission + /producer/me read scope.Current

        // The principal carries the tenant_id claim the HttpTenantContext path reads (S4) so the existing
        // CreateProductCommand(tenant.TenantId, ...) keeps working — NO role claim (the dual-scheme policy must not
        // resolve a producer as a tenant-Bearer principal; S3), and NEVER ITenantScope.Begin (double-bind throws).
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("tenant_id", resolution.TenantId.ToString()));
        if (!string.IsNullOrEmpty(resolved.Subject))
            identity.AddClaim(new Claim("sub", resolved.Subject));
        identity.AddClaim(new Claim("email", resolution.Email));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resolution.TenantUserId.ToString()));
        var principal = new ClaimsPrincipal(identity);

        // Rotation + idle-slide apply only to a live Active session (a grace predecessor is already superseded).
        if (session.Status == ProducerSessionStatus.Active)
        {
            if (now - session.IssuedAt >= policy.Rotation)
                await TryRotateAsync(session, now, policy, ct);
            else
                await MaybeSlideIdleAsync(session, now, policy, ct);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private async Task TryRotateAsync(ProducerSession session, DateTime now, ProducerSessionPolicy policy, CancellationToken ct)
    {
        var newToken = ProducerTokens.NewOpaqueToken();
        var csrfToken = ProducerTokens.NewOpaqueToken();
        var successor = session.Rotate(ProducerTokens.Hash(newToken), now, policy);

        // Atomic single-winner supersede (REQ-11.5): a concurrent request that already rotated wins; we serve under
        // grace with the existing cookie (no Set-Cookie) — exactly one successor is created.
        if (!await _sessions.TrySupersedeAsync(session.Id, successor.Id, now, ct))
            return;

        _sessions.Add(successor);
        _audit.Append(ProducerAuthAudit.For(ProducerAuthEventType.Rotated, Context.TraceIdentifier, now, session.ProducerAccountId));
        await _sessions.SaveChangesAsync(ct);
        _cookies.Write(Context, newToken, csrfToken); // safe: UseAuthentication runs before the response body
    }

    private async Task MaybeSlideIdleAsync(ProducerSession session, DateTime now, ProducerSessionPolicy policy, CancellationToken ct)
    {
        // Lazy: persist at most ~once a minute (REQ-10.4), bounded by the absolute expiry.
        var lastSlide = session.IdleExpiresAt - policy.Idle;
        if (now - lastSlide < TimeSpan.FromMinutes(1))
            return;
        var newIdle = now + policy.Idle;
        if (newIdle > session.AbsoluteExpiresAt)
            newIdle = session.AbsoluteExpiresAt;
        await _sessions.SlideIdleAsync(session.Id, newIdle, ct);
    }
}

/// <summary>READ-ONLY per-request producer resolution behind the source-generated mediator, so the auth handler's
/// decision/principal/rotation logic can be unit-tested without it (mirrors <see cref="IProducerCallbackResolver"/>).</summary>
internal interface IProducerSessionResolver
{
    Task<ProducerByIdResult> ResolveByIdAsync(Guid tenantUserId, CancellationToken cancellationToken);
}

internal sealed class ProducerSessionResolver(IMediator mediator) : IProducerSessionResolver
{
    public Task<ProducerByIdResult> ResolveByIdAsync(Guid tenantUserId, CancellationToken cancellationToken) =>
        mediator.Send(new ResolveProducerByIdQuery(tenantUserId), cancellationToken).AsTask();
}

/// <summary>Per-request holder of the resolved producer (REQ-17.1). The producer session authentication handler
/// calls <see cref="Set"/> once per request; readers consume <see cref="IProducerScope"/>. Fail-closed: a
/// tenant-Bearer caller binds nothing, so <c>RequireProducerPermission</c> denies it 403 (F10).</summary>
internal sealed class ProducerScope : IProducerScope
{
    private ProducerResolution? _current;

    public bool IsBound => _current is not null;
    public ProducerResolution Current => _current ?? throw new InvalidOperationException("No producer is bound to this request.");

    public void Set(ProducerResolution resolution) => _current = resolution;
}

internal static class ProducerSessionSchemeRegistration
{
    /// <summary>Registers the ProducerSession cookie scheme and the DUAL-SCHEME <c>producer</c> authorization policy:
    /// it admits EITHER the producer session OR the existing tenant Bearer scheme and requires only an authenticated
    /// user — NO <c>RequireClaim</c> (S3), so restoring the write-endpoint gates does not break tenant-Bearer callers
    /// (REQ-17.3). A tenant-Bearer caller passes the policy but binds no producer scope → <c>RequireProducerPermission</c>
    /// fail-closes 403 (REQ-17.2/F10).</summary>
    public static IServiceCollection AddProducerSessionScheme(this IServiceCollection services)
    {
        services.AddScoped<IProducerSessionResolver, ProducerSessionResolver>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ProducerSessionAuthenticationHandler>(
                ProducerSessionAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy("producer", policy => policy
                .AddAuthenticationSchemes(
                    ProducerSessionAuthenticationHandler.SchemeName, JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());

        return services;
    }
}
