using Payments.Domain;
using Payments.Domain.Psp;

namespace Payments.Application.Ports;

/// <summary>
/// Persistence port for <see cref="Session"/> aggregates. Implementations live in
/// Payments.Infrastructure over the shared txn data plane; the unit of work commits separately.
/// </summary>
public interface ISessionRepository
{
    /// <summary>Tracks a new session for insertion. Commit happens via the unit of work.</summary>
    void Add(Session session);

    Task<Session?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken);

    /// <summary>Looks a session up by the (PSP, external charge id) pair the webhook path resolves on.</summary>
    Task<Session?> GetByExternalChargeAsync(Code psp, string externalChargeId, CancellationToken cancellationToken);
}
