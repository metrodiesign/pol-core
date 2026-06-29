namespace Producer.Application;

/// <summary>
/// An ACTIVE producer's identity + reach, materialized once per request into <see cref="IProducerScope"/> by the
/// session authentication handler (REQ-17.1). The tenant + effective permissions are resolved fresh from the record,
/// NEVER from a token claim. Drives the ambient <c>tenant_id</c> claim (the <c>HttpTenantContext</c> path) and the
/// <c>RequireProducerPermission</c> gate.
/// </summary>
// ponytail: DUPLICATE-shaped of Admin.Application.AdminResolution (+ explicit TenantId, owner TenantUser) — deliberate debt.
public sealed record ProducerResolution(Guid TenantUserId, string Email, Guid TenantId, IReadOnlySet<string> Permissions);

/// <summary>
/// Per-request holder of the resolved producer (REQ-17.1). The producer session authentication handler calls
/// <c>Set</c> once per request; readers consume this seam. Fail-closed: a request that authenticates via the tenant
/// Bearer scheme (REQ-17.3) binds no producer, leaving <see cref="IsBound"/> false — so
/// <c>RequireProducerPermission</c> denies it 403 even though authentication passed (REQ-17.2/F10).
/// </summary>
public interface IProducerScope
{
    bool IsBound { get; }
    ProducerResolution Current { get; }
}
