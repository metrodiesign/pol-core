using Payments.Domain;

namespace Payments.Application.Ports;

/// <summary>Read port for tenant PSP connections (credentials binding + enabled methods).</summary>
public interface IPspConnectionRepository
{
    /// <summary>Resolves a tenant's connection for a given PSP.</summary>
    Task<PspConnection?> GetAsync(Guid tenantId, PspCode psp, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a connection by its own id. The webhook path routes on this id (never on a value
    /// parsed from the raw URL before signature verification — PLAN #4 / security rules).
    /// </summary>
    Task<PspConnection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Stages a new connection for insertion (tenant provisioning). Persisted on the next
    /// unit-of-work save; the caller owns the transaction.
    /// </summary>
    void Add(PspConnection connection);

    /// <summary>Lists all connections owned by a tenant (admin read-back).</summary>
    Task<IReadOnlyList<PspConnection>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken);
}
