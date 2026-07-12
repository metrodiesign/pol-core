using Checkouts.Domain;

namespace Checkouts.Application;

/// <summary>Persistence port for the <see cref="Session"/> aggregate. The Infrastructure
/// adapter resolves it against the shared shop data plane; all access is merchant-scoped by the
/// data-layer RLS floor.</summary>
public interface ICheckoutRepository
{
    /// <summary>Tracks a new session for insertion on the next unit-of-work save.</summary>
    void Add(Session session);

    /// <summary>Loads a session by id, or <c>null</c> when none exists in the caller's merchant.</summary>
    Task<Session?> GetByIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken);
}
