using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Orders.Application;
using Orders.Domain;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// EF Core implementation of <see cref="IOrderRepository"/> over the MerchantRuntime data plane.
/// Reads/writes go through <c>MerchantRuntimeDbContext.Set&lt;Order&gt;()</c>; merchant isolation is
/// enforced by the query filter + sealed write guard. Saving is the caller's responsibility via
/// <c>IUnitOfWork</c>. Scoped — depends on the Scoped DbContext.
/// </summary>
internal sealed class OrderRepository : IOrderRepository, IOrderStore
{
    private readonly MerchantRuntimeDbContext _db;

    public OrderRepository(MerchantRuntimeDbContext db) => _db = db;

    public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct), cancellationToken);

    public async Task<Order?> GetForUpdateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (_db.Database.IsSqlServer())
        {
            var locked = await PlatformReadGuard.ReadAsync(ct => _db.Database
                .SqlQueryRaw<Guid>(
                    "SELECT Id AS Value FROM shop.Orders WITH (UPDLOCK,HOLDLOCK) WHERE Id = @p0 AND MerchantId = @p1",
                    new SqlParameter("@p0", orderId),
                    new SqlParameter("@p1", _db.CurrentMerchant))
                .ToListAsync(ct), cancellationToken).ConfigureAwait(false);
            if (locked.Count == 0)
                return null;
        }

        return await PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct), cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Where(o => o.MerchantId == merchantId)
            .GroupBy(o => new { o.Status, o.Amount.Currency })
            .Select(g => new OrderStatusTotal(g.Key.Status, g.Key.Currency, g.Count(), g.Sum(o => o.Amount.Amount)))
            .ToListAsync(ct), cancellationToken);

    public async Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, string? orderNo, CancellationToken cancellationToken) =>
        await PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .Where(o => o.MerchantId == merchantId)
            .Where(o => orderNo == null || o.OrderNo == orderNo)
            .Include(o => o.Items)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct), cancellationToken);

    public void Add(Order order) => _db.Set<Order>().Add(order);
}
