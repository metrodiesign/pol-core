using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Payments.Application.Ports;
using SharedKernel;

namespace Persistence.MerchantRuntime.Payments;

/// <summary>
/// EF Core implementation of <see cref="IPayableOrderReader"/> over the MerchantRuntime data plane. It
/// lives here (not in Payments) because this assembly already maps both clusters, so the payment path can
/// read an order without Payments referencing Orders. The merchant floor is the context's global query
/// filter — a cross-merchant id simply finds no row, so a foreign order is reported as missing rather than
/// forbidden (no existence leak). Scalars are projected and <see cref="Money"/> re-composed in memory
/// (the repo's pattern — see <c>OrderRepository.GetReconciliationAsync</c> /
/// <c>OrderSummaryReader.GetByTokenAsync</c>), never projecting the complex type as a whole.
/// </summary>
internal sealed class PayableOrderReader : IPayableOrderReader
{
    private readonly MerchantRuntimeDbContext _db;

    public PayableOrderReader(MerchantRuntimeDbContext db) => _db = db;

    public async Task<PayableOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var row = await _db.Set<Order>()
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new { o.Id, o.Amount.Amount, o.Amount.Currency, o.Status })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new PayableOrder(row.Id, Money.Of(row.Amount, row.Currency), row.Status == OrderStatus.AwaitingPayment);
    }

    public async Task<PayableOrder?> GetForMintAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // UPDLOCK holds the order row until the surrounding transaction commits, so a concurrent cancel's
        // UPDATE waits behind this mint (and this mint waits behind a cancel that got there first — its
        // re-read then sees Cancelled and refuses). FromSql on the entity set, not Database.SqlQueryRaw,
        // so the merchant query filter still composes around the raw SELECT. SQL Server-only, like
        // OrderSummaryReader's raw reads.
        var row = await _db.Set<Order>()
            .FromSqlInterpolated($"SELECT * FROM shop.Orders WITH (UPDLOCK) WHERE Id = {orderId}")
            .AsNoTracking()
            .Select(o => new { o.Id, o.Amount.Amount, o.Amount.Currency, o.Status })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new PayableOrder(row.Id, Money.Of(row.Amount, row.Currency), row.Status == OrderStatus.AwaitingPayment);
    }
}
