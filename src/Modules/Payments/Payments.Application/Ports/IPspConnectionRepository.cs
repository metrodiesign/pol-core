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
}
