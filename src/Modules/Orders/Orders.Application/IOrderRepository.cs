using Orders.Domain;

namespace Orders.Application;

/// <summary>
/// Persistence port for the Order aggregate. Implemented in Infrastructure over the shared
/// <c>PolDbContext</c>; application handlers depend on this seam, never on a DbContext
/// directly (Clean Architecture dependency direction). Saving is the caller's job via
/// <c>IUnitOfWork</c> so the write commits atomically with any outbox/idempotency rows.
/// </summary>
public interface IOrderRepository
{
    /// <summary>Loads one order by id (RLS scopes it to the bound merchant), or null if absent.</summary>
    Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Loads the order created from a checkout session, or null — the idempotency lookup for the
    /// CheckoutConfirmed consumer (don't create a second order for the same checkout).</summary>
    Task<Order?> GetByCheckoutSessionIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken);

    /// <summary>Reconciliation read: the bound merchant's orders grouped by status + currency (count + total).</summary>
    Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken cancellationToken);

    /// <summary>Lists the bound merchant's orders, newest first, with their lines loaded (REQ-7.4 masked
    /// list surface — masking itself happens at the read-model projection, not here).</summary>
    Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, CancellationToken cancellationToken);

    /// <summary>Tracks a new order for insertion on the next unit-of-work save.</summary>
    void Add(Order order);
}
