using System.Security.Claims;
using BuildingBlocks.Application;

namespace TenantConsole;

/// <summary>
/// Resolves the ambient tenant from the authenticated principal's <c>tenant_id</c> claim (never from
/// a URL path — PLAN decision #4). Falls back to a configured dev tenant so local flows work before
/// real Google SSO claims are wired. Tenant console is never the admin cross-tenant principal.
/// Registered Scoped (per request).
/// </summary>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly Guid? _tenantId;

    public HttpTenantContext(IHttpContextAccessor accessor, IConfiguration configuration)
    {
        var claim = accessor.HttpContext?.User.FindFirstValue("tenant_id");
        if (Guid.TryParse(claim, out var fromClaim))
        {
            _tenantId = fromClaim;
        }
        else if (Guid.TryParse(configuration["Tenant:DevTenantId"], out var devTenant))
        {
            // ponytail: dev fallback tenant — production must carry a verified tenant_id claim and
            // this configured fallback should be removed (or left empty) outside Development.
            _tenantId = devTenant;
        }
    }

    public Guid TenantId =>
        _tenantId ?? throw new InvalidOperationException("No tenant is bound to the current request.");

    public bool IsAdmin => false;

    public bool HasTenant => _tenantId.HasValue;
}
