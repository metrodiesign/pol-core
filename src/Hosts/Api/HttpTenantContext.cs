using System.Security.Claims;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;

namespace Api;

/// <summary>
/// Resolves the ambient tenant. Order of precedence:
/// <list type="number">
///   <item>An explicit <see cref="AmbientTenant"/> binding — set by the webhook after it resolves the
///   tenant from the PSP connection id (the webhook is unauthenticated, so it has no claim).</item>
///   <item>The authenticated principal's <c>tenant_id</c> claim (never a URL path — PLAN decision #4).</item>
///   <item>A configured dev-only fallback tenant, so local flows work before real Google SSO is wired.</item>
/// </list>
/// Tenant console is never the admin cross-tenant principal. Registered Scoped (per request).
/// </summary>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly AmbientTenant _ambient;
    private readonly Guid? _claimTenantId;

    public HttpTenantContext(IHttpContextAccessor accessor, IConfiguration configuration, AmbientTenant ambient)
    {
        _ambient = ambient;

        var claim = accessor.HttpContext?.User.FindFirstValue("tenant_id");
        if (Guid.TryParse(claim, out var fromClaim))
        {
            _claimTenantId = fromClaim;
        }
        else if (Guid.TryParse(configuration["Tenant:DevTenantId"], out var devTenant))
        {
            // ponytail: dev fallback tenant — production must carry a verified tenant_id claim and
            // this configured fallback should be removed (or left empty) outside Development.
            _claimTenantId = devTenant;
        }
    }

    public Guid TenantId =>
        _ambient.IsBound ? _ambient.TenantId
        : _claimTenantId ?? throw new InvalidOperationException("No tenant is bound to the current request.");

    public bool IsAdmin => false;

    public bool HasTenant => _ambient.IsBound || _claimTenantId.HasValue;
}
