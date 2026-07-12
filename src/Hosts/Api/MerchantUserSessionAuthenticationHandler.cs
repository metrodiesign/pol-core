using System.Security.Claims;
using System.Text.Encodings.Web;
using BuildingBlocks.Application;
using Mediator;
using Merchants.Application;
using Merchants.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Api;

/// <summary>
/// Authenticates every merchant-user-scoped request via the opaque <c>__Host-mch_session</c> cookie (REQ-11/12/17).
/// It looks the session up by its SHA-256 hash, runs the decision table (REQ-11), re-resolves the merchant user's
/// current Status/Merchant/effective permissions READ-ONLY (REQ-12.4/17.1), builds a principal carrying the
/// <c>merchant_id</c> claim the existing <see cref="HttpActorContext"/> path reads (S4 — NOT
/// <see cref="IActorScope.Begin"/>), binds <see cref="IMerchantUserScope"/> for
/// <c>RequireMerchantUserPermission</c>, and transparently rotates the cookie past the rotation age (REQ-11.1). No
/// cookie -&gt; NoResult (the single-scheme <c>merchant-user</c> policy then denies 401 — REQ-17.3/T11, the Bearer
/// fallback is retired); a session exists only for an Active merchant user (REQ-10.1), so a suspend/reject denies
/// the next request (REQ-12.4).
/// </summary>
// ponytail: DUPLICATE of Api.AdminSessionAuthenticationHandler (PlatformUserId -> MerchantUserId; admin_tier claim ->
// merchant_id claim) — deliberate debt.
internal sealed class MerchantUserSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "MerchantUserSession";

    private readonly IMerchantUserSessionStore _sessions;
    private readonly IMerchantAuthAuditWriter _audit;
    private readonly MerchantUserSessionCookies _cookies;
    private readonly IMerchantUserSessionResolver _resolver;
    private readonly MerchantUserScope _scope;
    private readonly IClock _clock;
    private readonly MerchantUserSessionOptions _options;

    public MerchantUserSessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IMerchantUserSessionStore sessions,
        IMerchantAuthAuditWriter audit,
        MerchantUserSessionCookies cookies,
        IMerchantUserSessionResolver resolver,
        MerchantUserScope scope,
        IClock clock,
        IOptions<MerchantUserSessionOptions> sessionOptions)
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

    private MerchantUserSessionPolicy Policy => new(
        TimeSpan.FromMinutes(_options.IdleMinutes),
        TimeSpan.FromHours(_options.AbsoluteHours),
        TimeSpan.FromMinutes(_options.RotationMinutes),
        TimeSpan.FromSeconds(_options.GraceSeconds));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = _cookies.ReadSessionToken(Context);
        if (token is null)
            return AuthenticateResult.NoResult(); // no cookie -> the merchant-user policy denies 401 (single-scheme, T11)

        var ct = Context.RequestAborted;
        var session = await _sessions.FindByTokenHashAsync(MerchantUserTokens.Hash(token), ct);
        if (session is null)
            return AuthenticateResult.Fail("Unknown session.");

        var now = _clock.UtcNow;
        var policy = Policy;

        var familyActiveId = session.Status == MerchantUserSessionStatus.Superseded
            ? await _sessions.GetFamilyActiveSessionIdAsync(session.FamilyId, ct)
            : null;

        switch (MerchantUserSessionDecisionPolicy.Decide(session, familyActiveId, now, policy))
        {
            case MerchantUserSessionDecision.Reject:
                return AuthenticateResult.Fail("Session is revoked or expired."); // 401 (REQ-11.3/11.4)

            case MerchantUserSessionDecision.ReuseRevokeFamily:
                await _sessions.RevokeFamilyAsync(session.FamilyId, ct);
                _audit.Append(MerchantAuthAudit.For(MerchantAuthEventType.FamilyRevokedReuse, Context.TraceIdentifier, now,
                    session.MerchantUserId, reason: "reuse-detected"));
                await _audit.SaveChangesAsync(ct);
                return AuthenticateResult.Fail("Session reuse detected."); // 401, family killed (REQ-11.3)

            case MerchantUserSessionDecision.ServeActive:
            case MerchantUserSessionDecision.ServeUnderGrace:
            default:
                break;
        }

        // Per-request READ-ONLY resolution (REQ-12.4/17.1): a suspend/reject/role-change takes effect within one request.
        var resolved = await _resolver.ResolveByIdAsync(session.MerchantUserId, ct);
        if (resolved.Outcome != MerchantUserByIdOutcome.Resolved || resolved.Resolution is null)
            return AuthenticateResult.Fail("Merchant user is not active or no longer exists."); // suspend -> next request 401

        var resolution = resolved.Resolution;
        _scope.Set(resolution); // bind IMerchantUserScope so RequireMerchantUserPermission + /merchant-users/me read scope.Current

        // The principal carries the merchant_id claim the HttpActorContext path reads (S4) so the existing
        // CreateProductCommand(actor.MerchantId, ...) keeps working — NO role claim (T11 — single-scheme, no more
        // Bearer principal to distinguish from), and NEVER IActorScope.Begin (double-bind throws).
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("merchant_id", resolution.MerchantId.ToString()));
        if (!string.IsNullOrEmpty(resolved.Subject))
            identity.AddClaim(new Claim("sub", resolved.Subject));
        identity.AddClaim(new Claim("email", resolution.Email));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resolution.MerchantUserId.ToString()));
        var principal = new ClaimsPrincipal(identity);

        // Rotation + idle-slide apply only to a live Active session (a grace predecessor is already superseded).
        if (session.Status == MerchantUserSessionStatus.Active)
        {
            if (now - session.IssuedAt >= policy.Rotation)
                await TryRotateAsync(session, now, policy, ct);
            else
                await MaybeSlideIdleAsync(session, now, policy, ct);
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private async Task TryRotateAsync(MerchantUserSession session, DateTime now, MerchantUserSessionPolicy policy, CancellationToken ct)
    {
        var newToken = MerchantUserTokens.NewOpaqueToken();
        var csrfToken = MerchantUserTokens.NewOpaqueToken();
        var successor = session.Rotate(MerchantUserTokens.Hash(newToken), now, policy);

        // Atomic single-winner supersede (REQ-11.5): a concurrent request that already rotated wins; we serve under
        // grace with the existing cookie (no Set-Cookie) — exactly one successor is created.
        if (!await _sessions.TrySupersedeAsync(session.Id, successor.Id, now, ct))
            return;

        _sessions.Add(successor);
        _audit.Append(MerchantAuthAudit.For(MerchantAuthEventType.Rotated, Context.TraceIdentifier, now, session.MerchantUserId));
        await _sessions.SaveChangesAsync(ct);
        _cookies.Write(Context, newToken, csrfToken); // safe: UseAuthentication runs before the response body
    }

    private async Task MaybeSlideIdleAsync(MerchantUserSession session, DateTime now, MerchantUserSessionPolicy policy, CancellationToken ct)
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

/// <summary>READ-ONLY per-request merchant-user resolution behind the source-generated mediator, so the auth
/// handler's decision/principal/rotation logic can be unit-tested without it (mirrors
/// <see cref="IMerchantUserCallbackResolver"/>).</summary>
internal interface IMerchantUserSessionResolver
{
    Task<MerchantUserByIdResult> ResolveByIdAsync(Guid merchantUserId, CancellationToken cancellationToken);
}

internal sealed class MerchantUserSessionResolver(IMediator mediator) : IMerchantUserSessionResolver
{
    public Task<MerchantUserByIdResult> ResolveByIdAsync(Guid merchantUserId, CancellationToken cancellationToken) =>
        mediator.Send(new ResolveMerchantUserByIdQuery(merchantUserId), cancellationToken).AsTask();
}

/// <summary>Per-request holder of the resolved merchant user (REQ-17.1). The merchant-user session authentication
/// handler calls <see cref="Set"/> once per request; readers consume <see cref="IMerchantUserScope"/>. Fail-closed:
/// an unauthenticated caller binds nothing, so <c>RequireMerchantUserPermission</c> denies it 403 (F10).</summary>
internal sealed class MerchantUserScope : IMerchantUserScope
{
    private MerchantUserResolution? _current;

    public bool IsBound => _current is not null;
    public MerchantUserResolution Current => _current ?? throw new InvalidOperationException("No merchant user is bound to this request.");

    public void Set(MerchantUserResolution resolution) => _current = resolution;
}

internal static class MerchantUserSessionSchemeRegistration
{
    /// <summary>Registers the MerchantUserSession cookie scheme and the SINGLE-SCHEME <c>merchant-user</c>
    /// authorization policy (T11 — the dual-scheme Bearer fallback is retired): it admits only the merchant-user
    /// session and requires an authenticated user. Every funnel endpoint that used to gate on the "tenant" policy
    /// now also gates on this one (REQ-6/design "Auth policies").</summary>
    public static IServiceCollection AddMerchantUserSessionScheme(this IServiceCollection services)
    {
        services.AddScoped<IMerchantUserSessionResolver, MerchantUserSessionResolver>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, MerchantUserSessionAuthenticationHandler>(
                MerchantUserSessionAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy("merchant-user", policy => policy
                .AddAuthenticationSchemes(MerchantUserSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }
}
