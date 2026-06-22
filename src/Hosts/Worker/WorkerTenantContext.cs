using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;

namespace Worker;

/// <summary>
/// The worker has no HTTP request and no authenticated principal: its tenant is whatever the outbox
/// dispatcher binds per message via <see cref="AmbientTenant"/>. When nothing is bound (the lease
/// pass), <see cref="HasTenant"/> is false so the RLS interceptor leaves <c>SESSION_CONTEXT</c>
/// unset — correct, because the OutboxMessages table is read across tenants. The worker is never the
/// admin cross-tenant principal; per-message it runs RLS-scoped to exactly the message's tenant.
/// </summary>
public sealed class WorkerTenantContext : ITenantContext
{
    private readonly AmbientTenant _ambient;

    public WorkerTenantContext(AmbientTenant ambient) => _ambient = ambient;

    public Guid TenantId => _ambient.TenantId;
    public bool HasTenant => _ambient.IsBound;
}
