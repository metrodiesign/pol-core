using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain;

namespace Orders.Infrastructure;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/> over the shared producer data plane.
/// Reads/writes go through <c>PolDbContext.Set&lt;Order&gt;()</c>; merchant isolation is enforced
/// by the RLS interceptor + session context, so this never filters tenants in raw SQL. Saving is the
/// caller's responsibility via <c>IUnitOfWork</c>. Scoped — depends on the Scoped DbContext.
/// </summary>
public sealed class OrderRepository : IOrderRepository
{
    private readonly PolDbContext _db;

    public OrderRepository(PolDbContext db) => _db = db;

    public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        _db.Set<Order>().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public Task<Order?> GetByCheckoutSessionIdAsync(Guid checkoutSessionId, CancellationToken cancellationToken) =>
        _db.Set<Order>().FirstOrDefaultAsync(o => o.CheckoutSessionId == checkoutSessionId, cancellationToken);

    public async Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<Order>()
            .Where(o => o.MerchantId == merchantId)
            .GroupBy(o => new { o.Status, o.Amount.Currency })
            .Select(g => new OrderStatusTotal(g.Key.Status, g.Key.Currency, g.Count(), g.Sum(o => o.Amount.Amount)))
            .ToListAsync(cancellationToken);

    public void Add(Order order) => _db.Set<Order>().Add(order);
}
