using System.Security.Claims;
using System.Text.Encodings.Web;
using BuildingBlocks.Application;
using Mediator;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Api.Merchants;

/// <summary>
/// Authenticates every merchant-user-scoped request via the opaque <c>__Host-mch_session</c> cookie (REQ-11/12/17).
/// It looks the session up by its SHA-256 hash, runs the decision table (REQ-11), re-resolves the merchant user's
/// current Status/Merchant/effective permissions READ-ONLY (REQ-12.4/17.1), builds a principal carrying the
/// <c>merchant_id</c> claim the existing <see cref="HttpActorContext"/> path reads (S4 — NOT
/// <see cref="IActorScope.Begin"/>), binds <see cref="IMerchantUserScope"/> for
/// <c>RequirePermission</c>, and transparently rotates the cookie past the rotation age (REQ-11.1). No
/// cookie -&gt; NoResult (the single-scheme <c>merchant-user</c> policy then denies 401 — REQ-17.3/T11, the Bearer
/// fallback is retired); a session exists only for an Active merchant user (REQ-10.1), so a suspend/reject denies
/// the next request (REQ-12.4).
/// </summary>
// ponytail: DUPLICATE of Api.AdminSessionAuthenticationHandler (AdminUserId -> UserId; admin_tier claim ->
// merchant_id claim) — deliberate debt.
internal sealed class UserSessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "MerchantUserSession";

    private readonly ISessionStore _sessions;
    private readonly IAuthAuditWriter _audit;
    private readonly UserSessionCookies _cookies;
    private readonly IUserSessionResolver _resolver;
    private readonly UserScope _scope;
    private readonly IClock _clock;
    private readonly UserSessionOptions _options;

    public UserSessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionStore sessions,
        IAuthAuditWriter audit,
        UserSessionCookies cookies,
        IUserSessionResolver resolver,
        UserScope scope,
        IClock clock,
        IOptions<UserSessionOptions> sessionOptions)
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
            return AuthenticateResult.NoResult(); // no cookie -> the merchant-user policy denies 401 (single-scheme, T11)

        var ct = Context.RequestAborted;
        var session = await _sessions.FindByTokenHashAsync(UserTokens.Hash(token), ct);
        if (session is null)
            return AuthenticateResult.Fail("Unknown session.");

        var now = _clock.UtcNow;
        var policy = Policy;

        // Expired known tokens stay 401. Lifecycle is resolved only for a token still inside both windows.
        if (now >= session.IdleExpiresAt || now >= session.AbsoluteExpiresAt)
            return AuthenticateResult.Fail("Session is expired.");

        var resolved = await _resolver.ResolveByIdAsync(session.UserId, ct);
        if (resolved.Outcome != ByIdOutcome.Resolved || resolved.Resolution is null)
        {
            var code = resolved.Status switch
            {
                UserStatus.PendingApproval => "awaiting-approval",
                UserStatus.Rejected => "rejected",
                UserStatus.Suspended => "suspended",
                UserStatus.Active when resolved.MerchantId is null => "unbound",
                _ => null,
            };
            if (code is not null)
                Context.Features.Set(new MerchantLifecycleChallenge(code));
            return AuthenticateResult.Fail("Merchant user is not active or no longer exists.");
        }

        var familyActiveId = session.Status == SessionStatus.Superseded
            ? await _sessions.GetFamilyActiveSessionIdAsync(session.FamilyId, ct)
            : null;

        switch (SessionDecisionPolicy.Decide(session, familyActiveId, now, policy))
        {
            case SessionDecision.Reject:
                return AuthenticateResult.Fail("Session is revoked or expired."); // 401 (REQ-11.3/11.4)

            case SessionDecision.ReuseRevokeFamily:
                await _sessions.RevokeFamilyAsync(session.FamilyId, ct);
                _audit.Append(AuthAudit.For(AuthEventType.FamilyRevokedReuse, Context.TraceIdentifier, now,
                    session.UserId, reason: "reuse-detected"));
                await _audit.SaveChangesAsync(ct);
                return AuthenticateResult.Fail("Session reuse detected."); // 401, family killed (REQ-11.3)

            case SessionDecision.ServeActive:
            case SessionDecision.ServeUnderGrace:
            default:
                break;
        }

        var resolution = resolved.Resolution;
        _scope.Set(resolution); // bind IMerchantUserScope so RequirePermission + /merchants/users/me read scope.Current

        // The principal carries the merchant_id claim the HttpActorContext path reads (S4) so the existing
        // CreateProductCommand(actor.MerchantId, ...) keeps working — NO role claim (T11 — single-scheme, no more
        // Bearer principal to distinguish from), and NEVER IActorScope.Begin (double-bind throws).
        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim("merchant_id", resolution.MerchantId.ToString()));
        if (!string.IsNullOrEmpty(resolved.Subject))
            identity.AddClaim(new Claim("sub", resolved.Subject));
        identity.AddClaim(new Claim("email", resolution.Email));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, resolution.UserId.ToString()));
        // The catalogue search runs under the account's own upstream sale code, never one the client picked
        // (REQ-4.8). Re-resolved with the rest of the account on every request, so revoking it takes effect on
        // the next one; absent when the account has none, which the catalogue path answers with 403 (REQ-4.9).
        if (!string.IsNullOrEmpty(resolution.SaleCode))
            identity.AddClaim(new Claim(HttpActorContext.SaleCodeClaim, resolution.SaleCode));
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

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (Context.Features.Get<MerchantLifecycleChallenge>() is not { } lifecycle)
        {
            await base.HandleChallengeAsync(properties);
            return;
        }

        Context.Response.StatusCode = StatusCodes.Status403Forbidden;
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Merchant user account is not active",
        };
        details.Extensions["code"] = lifecycle.Code;
        details.Extensions["traceId"] = Context.TraceIdentifier;
        await Context.Response.WriteAsJsonAsync(
            details, options: null, contentType: "application/problem+json", cancellationToken: Context.RequestAborted);
    }

    private async Task TryRotateAsync(Session session, DateTime now, SessionPolicy policy, CancellationToken ct)
    {
        var newToken = UserTokens.NewOpaqueToken();
        var csrfToken = UserTokens.NewOpaqueToken();
        var successor = session.Rotate(UserTokens.Hash(newToken), now, policy);

        // Atomic single-winner supersede (REQ-11.5): a concurrent request that already rotated wins; we serve under
        // grace with the existing cookie (no Set-Cookie) — exactly one successor is created.
        if (!await _sessions.TrySupersedeAsync(session.Id, successor.Id, now, ct))
            return;

        _sessions.Add(successor);
        _audit.Append(AuthAudit.For(AuthEventType.Rotated, Context.TraceIdentifier, now, session.UserId));
        await _sessions.SaveChangesAsync(ct);
        _cookies.Write(Context, newToken, csrfToken); // safe: UseAuthentication runs before the response body
    }

    private async Task MaybeSlideIdleAsync(Session session, DateTime now, SessionPolicy policy, CancellationToken ct)
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

internal sealed record MerchantLifecycleChallenge(string Code);

/// <summary>READ-ONLY per-request merchant-user resolution behind the source-generated mediator, so the auth
/// handler's decision/principal/rotation logic can be unit-tested without it (mirrors
/// <see cref="IMerchantUserCallbackResolver"/>).</summary>
internal interface IUserSessionResolver
{
    Task<ByIdResult> ResolveByIdAsync(Guid merchantUserId, CancellationToken cancellationToken);
}

internal sealed class UserSessionResolver(IMediator mediator) : IUserSessionResolver
{
    public Task<ByIdResult> ResolveByIdAsync(Guid merchantUserId, CancellationToken cancellationToken) =>
        mediator.Send(new ResolveByIdQuery(merchantUserId), cancellationToken).AsTask();
}

/// <summary>Per-request holder of the resolved merchant user (REQ-17.1). The merchant-user session authentication
/// handler calls <see cref="Set"/> once per request; readers consume <see cref="IMerchantUserScope"/>. Fail-closed:
/// an unauthenticated caller binds nothing, so <c>RequirePermission</c> denies it 403 (F10).</summary>
internal sealed class UserScope : IUserScope
{
    private Resolution? _current;

    public bool IsBound => _current is not null;
    public Resolution Current => _current ?? throw new InvalidOperationException("No merchant user is bound to this request.");

    public void Set(Resolution resolution) => _current = resolution;
}

internal static class UserSessionSchemeRegistration
{
    /// <summary>Registers the MerchantUserSession cookie scheme and the SINGLE-SCHEME <c>merchant-user</c>
    /// authorization policy (T11 — the dual-scheme Bearer fallback is retired): it admits only the merchant-user
    /// session and requires an authenticated user. Every funnel endpoint that used to gate on the "tenant" policy
    /// now also gates on this one (REQ-6/design "Auth policies").</summary>
    public static IServiceCollection AddMerchantUserSessionScheme(this IServiceCollection services)
    {
        services.AddScoped<IUserSessionResolver, UserSessionResolver>();
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, UserSessionAuthenticationHandler>(
                UserSessionAuthenticationHandler.SchemeName, _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy("merchant-user", policy => policy
                .AddAuthenticationSchemes(UserSessionAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());

        return services;
    }
}
