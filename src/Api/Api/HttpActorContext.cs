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
    private readonly IHttpContextAccessor _accessor;
    private readonly IConfiguration _configuration;
    private readonly AmbientActor _ambient;

    public HttpActorContext(IHttpContextAccessor accessor, IConfiguration configuration, AmbientActor ambient)
    {
        _accessor = accessor;
        _configuration = configuration;
        _ambient = ambient;
    }

    // Claims are read lazily PER ACCESS, never snapshotted at construction: this Scoped service gets
    // constructed DURING session authentication (auth handler ctor → ISessionStore → MerchantUserDbContext →
    // IActorContext) — BEFORE the handler sets the authenticated principal, so a constructor snapshot froze
    // CurrentMerchant at Guid.Empty for the whole request (bugfix-merchant-prebind-wiring F5, defect D3).
    private Guid? ClaimMerchantId
    {
        get
        {
            var merchantClaim = _accessor.HttpContext?.User.FindFirstValue("merchant_id");
            if (Guid.TryParse(merchantClaim, out var fromClaim))
                return fromClaim;
            // ponytail: dev fallback merchant — production must carry a verified merchant_id claim and
            // this configured fallback should be removed (or left empty) outside Development.
            if (Guid.TryParse(_configuration["Merchant:DevMerchantId"], out var devMerchant))
                return devMerchant;
            return null;
        }
    }

    private Guid? ClaimUserId =>
        Guid.TryParse(_accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var fromUserClaim)
            ? fromUserClaim
            : null;

    public Guid MerchantId =>
        _ambient.IsBound ? _ambient.MerchantId
        : ClaimMerchantId ?? throw new InvalidOperationException("No actor is bound to the current request.");

    public Guid? UserId => _ambient.IsBound ? _ambient.UserId : ClaimUserId;

    public bool HasActor => _ambient.IsBound || ClaimMerchantId.HasValue;

    /// <summary>Read per access from the <c>sale_code</c> claim the merchant-user session handler mints from the
    /// freshly resolved account (REQ-4.8) — same lazy rule as <see cref="ClaimMerchantId"/>, never snapshotted at
    /// construction (bugfix-merchant-prebind-wiring F5). An ambient (webhook) binding carries no merchant user,
    /// so it has no sale code; there is deliberately no dev fallback — a wrong code searches somebody else's
    /// catalogue.</summary>
    public string? SaleCode => _accessor.HttpContext?.User.FindFirstValue(SaleCodeClaim);

    /// <summary>Claim name, in the snake_case the OIDC/claim convention uses (not the C# property spelling).</summary>
    public const string SaleCodeClaim = "sale_code";
}
