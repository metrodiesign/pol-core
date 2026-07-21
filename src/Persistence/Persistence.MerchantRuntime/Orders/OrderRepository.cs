using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/> over the MerchantRuntime data plane.
/// Reads/writes go through <c>MerchantRuntimeDbContext.Set&lt;Order&gt;()</c>; merchant isolation is
/// enforced by the query filter + sealed write guard. Saving is the caller's responsibility via
/// <c>IUnitOfWork</c>. Scoped — depends on the Scoped DbContext.
/// </summary>
internal sealed class OrderRepository : IOrderRepository
{
    private readonly MerchantRuntimeDbContext _db;

    public OrderRepository(MerchantRuntimeDbContext db) => _db = db;

    public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        _db.Set<Order>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public Task<Order?> GetByCheckoutSessionIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        _db.Set<Order>().FirstOrDefaultAsync(o => o.CheckoutSessionId == checkoutSessionId, cancellationToken);

    public async Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<Order>()
            .Where(o => o.MerchantId == merchantId)
            .GroupBy(o => new { o.Status, o.Amount.Currency })
            .Select(g => new OrderStatusTotal(g.Key.Status, g.Key.Currency, g.Count(), g.Sum(o => o.Amount.Amount)))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<Order>()
            .Where(o => o.MerchantId == merchantId)
            .Include(o => o.Lines)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

    public void Add(Order order) => _db.Set<Order>().Add(order);
}
