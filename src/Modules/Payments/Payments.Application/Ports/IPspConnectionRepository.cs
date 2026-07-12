using Payments.Domain;

namespace Payments.Application.Ports;

/// <summary>Read port for merchant PSP connections (credentials binding + enabled methods).</summary>
public interface IPspConnectionRepository
{
    /// <summary>Resolves a merchant's connection for a given PSP.</summary>
    Task<PspConnection?> GetAsync(Guid merchantId, PspCode psp, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a connection by its own id. The webhook path routes on this id (never on a value
    /// parsed from the raw URL before signature verification — PLAN #4 / security rules).
    /// </summary>
    Task<PspConnection?> GetByIdAsync(Guid pspConnectionId, CancellationToken cancellationToken);

    /// <summary>
    /// Stages a new connection for insertion (merchant provisioning). Persisted on the next
    /// unit-of-work save; the caller owns the transaction.
    /// </summary>
    void Add(PspConnection connection);

    /// <summary>Lists all connections owned by a merchant (admin read-back).</summary>
    Task<IReadOnlyList<PspConnection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken);
}
