using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Application;
using Orders.Domain;

namespace Persistence.MerchantRuntime.Orders;

internal sealed class AdminOrderReader(
    MerchantRuntimeDbContext db,
    ILogger<AdminOrderReader> logger) : IAdminOrderReader
{
    public async Task<PagedResult<OrderListItem>> ListAsync(
        AdminOrderQuery query, CancellationToken cancellationToken)
    {
        if (query.MerchantId is { } selected && !query.Access.Allows(selected))
            return new PagedResult<OrderListItem>([], query.Page, query.Limit, 0);

        var source = db.Set<Order>().IgnoreQueryFilters().AsNoTracking();
        if (!query.Access.IsUnrestricted)
            source = source.Where(x => query.Access.MerchantIds.Contains(x.MerchantId));
        if (query.MerchantId is { } merchantId)
            source = source.Where(x => x.MerchantId == merchantId);
        source = source.ApplyFilters(query.Filters, logger).ApplySearch(query.Search);

        var total = await PlatformReadGuard.ReadAsync(ct => source.LongCountAsync(ct), cancellationToken);
        var skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);
        var rows = await PlatformReadGuard.ReadAsync(ct => source.ApplySort(query.Sort, logger)
            .Skip(skip).Take(query.Limit).Include(x => x.Items).AsSplitQuery().ToListAsync(ct), cancellationToken);
        return new PagedResult<OrderListItem>(rows.Select(Project).ToList(), query.Page, query.Limit, total);
    }

    public async Task<AdminOrderResource?> ResolveAsync(
        Guid orderId, AdminOrderAccess access, CancellationToken cancellationToken)
    {
        var row = await PlatformReadGuard.ReadAsync(ct => db.Set<Order>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == orderId)
            .Select(x => new AdminOrderResource(
                x.Id, x.MerchantId, x.OriginatorId, x.PaymentSessionId, x.Version))
            .SingleOrDefaultAsync(ct), cancellationToken);
        return row is not null && access.Allows(row.MerchantId) ? row : null;
    }

    public async Task<IReadOnlyList<OrderStatusTotal>> ReconciliationAsync(
        AdminOrderAccess access,
        Guid? merchantId,
        CancellationToken cancellationToken)
    {
        if (merchantId is { } selected && !access.Allows(selected))
            return [];

        var source = db.Set<Order>().IgnoreQueryFilters().AsNoTracking();
        if (!access.IsUnrestricted)
            source = source.Where(x => access.MerchantIds.Contains(x.MerchantId));
        if (merchantId is { } scopedMerchant)
            source = source.Where(x => x.MerchantId == scopedMerchant);

        return await PlatformReadGuard.ReadAsync(ct => source
            .GroupBy(x => new { x.Status, x.Amount.Currency })
            .OrderBy(group => group.Key.Status)
            .ThenBy(group => group.Key.Currency)
            .Select(group => new OrderStatusTotal(
                group.Key.Status,
                group.Key.Currency,
                group.Count(),
                group.Sum(x => x.Amount.Amount)))
            .ToListAsync(ct), cancellationToken);
    }

    private static OrderListItem Project(Order order) => new(
        order.Id, order.OrderNo, order.Status.ToString(), order.Amount, order.CreatedAt, order.PaymentChannel,
        order.CustomerName, order.CustomerPhone, order.CustomerEmail,
        order.Items.Select(x => new OrderItemListItem(
            x.ProductCode, x.VariantCode, x.VariantName, x.Quantity, x.UnitPrice, x.Discount)).ToList(),
        order.MerchantId, order.OriginatorId, order.PaymentSessionId,
        order.UpdatedAt, order.PaidAt, order.Version);
}
