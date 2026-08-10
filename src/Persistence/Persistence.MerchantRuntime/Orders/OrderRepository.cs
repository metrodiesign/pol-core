using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<OrderRepository> _logger;

    public OrderRepository(MerchantRuntimeDbContext db, ILogger<OrderRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    internal OrderRepository(MerchantRuntimeDbContext db)
        : this(db, NullLogger<OrderRepository>.Instance) { }

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

    public async Task<PagedResult<Order>> ListAsync(
        Guid merchantId,
        PagedQuery query,
        CancellationToken cancellationToken)
    {
        var source = _db.Set<Order>()
            .AsNoTracking()
            .Where(o => o.MerchantId == merchantId)
            .ApplyFilters(query.Filters, _logger);

        var total = await PlatformReadGuard.ReadAsync(
            ct => source.LongCountAsync(ct), cancellationToken).ConfigureAwait(false);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var items = await PlatformReadGuard.ReadAsync(ct => source
                .ApplySort(query.Sort, _logger)
                .Skip(skip)
                .Take(query.Limit)
                .Include(o => o.Items)
                .AsSplitQuery()
                .ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<Order>(items, query.Page, query.Limit, total);
    }

    public void Add(Order order) => _db.Set<Order>().Add(order);
}
