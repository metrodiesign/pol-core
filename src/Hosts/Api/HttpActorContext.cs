using System.Security.Claims;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;

namespace Api;

/// <summary>
/// Resolves the ambient actor. Order of precedence:
/// <list type="number">
///   <item>An explicit <see cref="AmbientActor"/> binding — set by the webhook after it resolves the
///   merchant from the PSP connection id (the webhook is unauthenticated, so it has no claim).</item>
///   <item>The authenticated principal's <c>merchant_id</c> claim + <see cref="ClaimTypes.NameIdentifier"/>
///   (never a URL path — PLAN decision #4).</item>
///   <item>A configured dev-only fallback merchant, so local flows work before real Google SSO is wired
///   (no matching dev UserId — a merchant-branch request with UserId unset is valid, REQ-3.10/T4).</item>
/// </list>
/// Merchant console is never the admin cross-merchant principal. Registered Scoped (per request).
/// </summary>
public sealed class HttpActorContext : IActorContext
{
    private readonly AmbientActor _ambient;
    private readonly Guid? _claimMerchantId;
    private readonly Guid? _claimUserId;

    public HttpActorContext(IHttpContextAccessor accessor, IConfiguration configuration, AmbientActor ambient)
    {
        _ambient = ambient;

        var merchantClaim = accessor.HttpContext?.User.FindFirstValue("merchant_id");
        if (Guid.TryParse(merchantClaim, out var fromClaim))
        {
            _claimMerchantId = fromClaim;
        }
        else if (Guid.TryParse(configuration["Merchant:DevMerchantId"], out var devMerchant))
        {
            // ponytail: dev fallback merchant — production must carry a verified merchant_id claim and
            // this configured fallback should be removed (or left empty) outside Development.
            _claimMerchantId = devMerchant;
        }

        var userClaim = accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(userClaim, out var fromUserClaim))
            _claimUserId = fromUserClaim;
    }

    public Guid MerchantId =>
        _ambient.IsBound ? _ambient.MerchantId
        : _claimMerchantId ?? throw new InvalidOperationException("No actor is bound to the current request.");

    public Guid? UserId => _ambient.IsBound ? _ambient.UserId : _claimUserId;

    public bool HasActor => _ambient.IsBound || _claimMerchantId.HasValue;
}
