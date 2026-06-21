using BuildingBlocks.Application;

namespace AdminConsole;

/// <summary>
/// The admin console runs under a cross-tenant DB principal (PLAN decision #3): it is not bound to a
/// single tenant. <see cref="IsAdmin"/> is true and <see cref="HasTenant"/> is false, so the RLS
/// session-context path treats it as the admin principal. Registered Scoped (per request).
/// </summary>
public sealed class AdminTenantContext : ITenantContext
{
    public Guid TenantId =>
        throw new InvalidOperationException("The admin console is not bound to a single tenant.");

    public bool IsAdmin => true;

    public bool HasTenant => false;
}
