using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using SharedKernel;
using OrderItem = Orders.Domain.Items.Item;

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
        var row = await PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(o => new PayableOrderRow(
                o.Id,
                o.MerchantId,
                o.Amount.Amount,
                o.Amount.Currency,
                o.Status,
                o.PaymentSessionId,
                o.PaymentChannel,
                o.InitiatingAudience,
                o.InitiatingMerchantUserId))
            .FirstOrDefaultAsync(ct), cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new PayableOrder(
                row.Id,
                Money.Of(row.Amount, row.Currency),
                Map(row.Status),
                row.PaymentSessionId,
                row.PaymentChannel,
                row.MerchantId,
                MapAudience(row.InitiatingAudience),
                row.InitiatingMerchantUserId);
    }

    public async Task<PayableOrder?> GetForMintAsync(Guid orderId, CancellationToken cancellationToken)
    {
        // UPDLOCK holds the order row until the surrounding transaction commits, so a concurrent cancel's
        // UPDATE waits behind this mint (and this mint waits behind a cancel that got there first — its
        // re-read then sees Cancelled and refuses). The lock is taken by a raw scalar SELECT (no complex-type
        // columns): composing a projection over FromSql makes EF forget the complex type's HasColumnName
        // mapping and emit Amount_Amount/Amount_Currency (invalid columns). The locked read then goes through
        // the same LINQ path as GetAsync — mapped columns, merchant query filter — inside the same transaction.
        // ToListAsync + Count == 0, not SingleAsync/FirstOrDefaultAsync: those compose the SqlQueryRaw into a
        // derived table, which is exactly what breaks NEXT VALUE FOR in OrderNoSequence (Msg 11719) — this
        // scalar read avoids the same trap by never composing over it.
        if (_db.Database.IsSqlServer())
        {
            var locked = await PlatformReadGuard.ReadAsync(ct => _db.Database
                .SqlQueryRaw<Guid>(
                    "SELECT Id AS Value FROM shop.Orders WITH (UPDLOCK,HOLDLOCK) WHERE Id = @p0 AND MerchantId = @p1",
                    new SqlParameter("@p0", orderId),
                    new SqlParameter("@p1", _db.CurrentMerchant))
                .ToListAsync(ct), cancellationToken)
                .ConfigureAwait(false);
            if (locked.Count == 0)
                return null;
        }

        return await GetAsync(orderId, cancellationToken).ConfigureAwait(false);
    }

    public async Task AttachAttemptAsync(
        Guid orderId,
        Guid paymentSessionId,
        CancellationToken cancellationToken)
    {
        var order = await PlatformReadGuard.ReadAsync(ct => _db.Set<Order>()
            .FirstOrDefaultAsync(o => o.Id == orderId, ct), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Order {orderId} disappeared while attaching payment attempt.");

        order.AttachPaymentAttempt(paymentSessionId, DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<DocumentKey>> GetDocumentKeysAsync(
        Guid orderId, CancellationToken cancellationToken)
    {
        // Merchant-filtered on purpose, unlike the sold-check itself: these are the keys of the CALLER'S OWN
        // order, and an order invisible under the floor simply has no lines to check.
        var rows = await PlatformReadGuard.ReadAsync(ct => _db.Set<OrderItem>()
            .AsNoTracking()
            .Where(item => item.OrderId == orderId)
            .Select(item => new { DocumentNo = item.ProductCode, ProductGroup = item.VariantCode })
            .ToListAsync(ct), cancellationToken)
            .ConfigureAwait(false);

        return rows.ConvertAll(row => new DocumentKey(row.DocumentNo, row.ProductGroup));
    }

    /// <summary>Total by hand rather than by cast: the two enums are independent contracts, and a status
    /// added to Orders must fail loudly here rather than fall through to "still payable".</summary>
    private static PayableOrderStatus Map(OrderStatus status) => status switch
    {
        OrderStatus.Pending => PayableOrderStatus.Pending,
        OrderStatus.Paid => PayableOrderStatus.Paid,
        OrderStatus.Failed => PayableOrderStatus.Failed,
        OrderStatus.Expired => PayableOrderStatus.Expired,
        OrderStatus.Refunded => PayableOrderStatus.Refunded,
        OrderStatus.Cancelled => PayableOrderStatus.Cancelled,
        _ => throw new NotSupportedException($"Order status {status} has no payment-path meaning."),
    };

    private static PaymentAudience? MapAudience(OrderInitiatingAudience? audience) => audience switch
    {
        null => null,
        OrderInitiatingAudience.User => PaymentAudience.User,
        OrderInitiatingAudience.PlatformAdmin => PaymentAudience.PlatformAdmin,
        _ => throw new NotSupportedException($"Order audience {audience} has no payment-path meaning."),
    };

    private sealed record PayableOrderRow(
        Guid Id,
        Guid MerchantId,
        decimal Amount,
        string Currency,
        OrderStatus Status,
        Guid? PaymentSessionId,
        string? PaymentChannel,
        OrderInitiatingAudience? InitiatingAudience,
        Guid? InitiatingMerchantUserId);
}
