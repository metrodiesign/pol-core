using BuildingBlocks.Application;
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

    Task<PagedResult<Session>> ListAsync(PagedQuery query, CancellationToken cancellationToken);

    /// <summary>Looks a session up by the (PSP, external charge id) pair the webhook path resolves on.</summary>
    Task<Session?> GetByExternalChargeAsync(Code psp, string externalChargeId, CancellationToken cancellationToken);

    /// <summary>
    /// The order's still-chargeable session — <see cref="SessionStatus.Created"/> or
    /// <see cref="SessionStatus.Redirected"/> — or null when the order has none. Returns the entity, not a
    /// bool, because create-session compares its method+PSP to decide between returning it (same channel,
    /// idempotent) and refusing (a different channel cannot be swapped in mid-flight). Terminal sessions
    /// (Paid/Failed/Expired) are deliberately excluded, so a failed attempt can be retried.
    /// </summary>
    Task<Session?> GetOpenForOrderAsync(Guid orderId, CancellationToken cancellationToken);
}
