using DomainTenant = Tenant.Domain.Tenant;

namespace Tenant.Application;

/// <summary>
/// Persistence port for the <see cref="DomainTenant"/> master record. Provisioning + admin read-back
/// only — bound to the pol_admin (RLS-bypass) connection by the host, so reads/writes see every tenant.
/// </summary>
public interface ITenantRepository
{
    /// <summary>Stages a new tenant for insertion. Persisted on the next unit-of-work save.</summary>
    void Add(DomainTenant tenant);

    /// <summary>Resolves a tenant by its normalized code, or null.</summary>
    Task<DomainTenant?> GetByCodeAsync(string normalizedCode, CancellationToken cancellationToken);

    /// <summary>True if a tenant with the normalized code already exists (idempotency pre-check).</summary>
    Task<bool> ExistsByCodeAsync(string normalizedCode, CancellationToken cancellationToken);
}
