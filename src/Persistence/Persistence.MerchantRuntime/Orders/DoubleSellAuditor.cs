using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Application;
using Orders.Domain;

namespace Persistence.MerchantRuntime.Orders;

/// <summary>
/// EF Core implementation of <see cref="IDoubleSellAuditor"/> (products-external-source-of-truth REQ-5.16,
/// REQ-8.2). Lives here for the two reasons the port records: this assembly can log, and it is the only one
/// that can read <c>shop.OrderItems</c> across merchants — the second buyer of a document is very often
/// another company, so a merchant-filtered read would report nothing exactly when there IS something.
/// <para>Nothing here blocks or repairs the sale: the order has already been paid at the PSP by the time this
/// runs. The platform's answer to a double sale is a human, and this is the page that wakes them.</para>
/// </summary>
internal sealed class DoubleSellAuditor : IDoubleSellAuditor
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly ILogger<DoubleSellAuditor> _logger;

    public DoubleSellAuditor(MerchantRuntimeDbContext db, ILogger<DoubleSellAuditor> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ReportIfDoubleSoldAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var mine = await _db.OrderItems.IgnoreQueryFilters()
            .Where(item => item.OrderId == orderId)
            .Select(item => new DocumentRow(item.DocumentNo, item.ProductGroup))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (mine.Count == 0)
            return;

        var documentNos = mine.ConvertAll(row => row.DocumentNo);

        // "item.OrderId != orderId" IS the second half of REQ-5.16: an outbox redelivery re-runs this for an
        // order that legitimately holds its own documents, and counting itself would page someone on every
        // retry. ProductGroup is not in the SQL predicate — it has four values and would not narrow the
        // IX_OrderItems_DocumentNo seek — so it is applied in memory below, exactly as the sold-check does.
        var clashes = await (
            from item in _db.OrderItems.IgnoreQueryFilters()
            join order in _db.Orders.IgnoreQueryFilters() on item.OrderId equals order.Id
            where item.OrderId != orderId
                && order.Status == OrderStatus.Paid
                && documentNos.Contains(item.DocumentNo)
            select new { item.DocumentNo, item.ProductGroup, OtherOrderId = order.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var clash in clashes)
        {
            // Same comparer as DocumentSaleProbe: linguistic, so the C# side never keeps apart two strings the
            // Thai_100_CI_AS collation folds together (which would silence a real double sale).
            if (!mine.Exists(own =>
                    string.Equals(own.DocumentNo, clash.DocumentNo, StringComparison.InvariantCultureIgnoreCase)
                    && string.Equals(own.ProductGroup, clash.ProductGroup, StringComparison.InvariantCultureIgnoreCase)))
                continue;

            _logger.LogCritical(
                "Orders: document {DocumentNo} ({ProductGroup}) was paid for by order {OrderId} and also by "
                + "order {OtherOrderId} — the same insurance document was sold twice.",
                clash.DocumentNo, clash.ProductGroup, orderId, clash.OtherOrderId);
        }
    }

    private sealed record DocumentRow(string DocumentNo, string ProductGroup);
}
