using BuildingBlocks.Application;
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

    /// <summary>Loads and tracks one Order while holding UPDLOCK,HOLDLOCK until caller transaction ends.</summary>
    Task<Order?> GetForUpdateAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Reconciliation read: the bound merchant's orders grouped by status + currency (count + total).</summary>
    Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken cancellationToken);

    /// <summary>Lists a stable, filtered page of the bound merchant's orders with their lines loaded.</summary>
    Task<PagedResult<Order>> ListAsync(
        Guid merchantId,
        PagedQuery query,
        CancellationToken cancellationToken);

    /// <summary>Tracks a new order for insertion on the next unit-of-work save.</summary>
    void Add(Order order);
}
