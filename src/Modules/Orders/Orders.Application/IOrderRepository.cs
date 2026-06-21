using Orders.Domain;

namespace Orders.Application;

/// <summary>
/// Persistence port for the Order aggregate. Implemented in Infrastructure over the shared
/// <c>ProducerDbContext</c>; application handlers depend on this seam, never on a DbContext
/// directly (Clean Architecture dependency direction). Saving is the caller's job via
/// <c>IUnitOfWork</c> so the write commits atomically with any outbox/idempotency rows.
/// </summary>
public interface IOrderRepository
{
    /// <summary>Loads the order awaiting the given payment session, or null if none exists.</summary>
    Task<Order?> GetByPaymentSessionIdAsync(Guid paymentSessionId, CancellationToken cancellationToken);

    /// <summary>Tracks a new order for insertion on the next unit-of-work save.</summary>
    void Add(Order order);
}
