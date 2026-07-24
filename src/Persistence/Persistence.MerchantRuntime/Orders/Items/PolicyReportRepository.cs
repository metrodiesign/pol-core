using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Application;

namespace Persistence.MerchantRuntime.Orders.Items;

/// <summary>
/// Merchant-plane implementation of <see cref="IPolicyReportRepository"/> (policy-reference-record REQ-4.1/
/// 4.2/4.4) — the ambient <see cref="MerchantRuntimeDbContext"/>; <see cref="PolicyReportSfs.BuildQuery"/> is
/// confined to <c>{ query.MerchantId }</c> both by the ambient query filter (already active, since
/// <paramref name="ignoreFilters"/> is false — see call site) AND the explicit confinement set below
/// (defence-in-depth, mirrors <c>ProductRepository.ListAsync</c>).
/// </summary>
internal sealed class PolicyReportRepository : IPolicyReportRepository
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly ILogger<PolicyReportRepository> _logger;

    public PolicyReportRepository(MerchantRuntimeDbContext db, ILogger<PolicyReportRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<PagedResult<PolicyReportItem>> ListAsync(ListPolicyReportQuery query, CancellationToken cancellationToken)
    {
        var confined = await PolicyReportSfs
            .BuildQuery(_db, ignoreFilters: false, confineToMerchants: new HashSet<Guid> { query.MerchantId })
            .ToListAsync(cancellationToken);   // bounded to one merchant — see PolicyReportSfs's own "ponytail" note

        var filtered = confined.ApplyFilters(query.Filters, _logger).ToList();

        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);   // overflow-safe offset
        var items = filtered
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Select(r => r.ToItem(includeMerchantId: false))
            .ToList();

        return new PagedResult<PolicyReportItem>(items, query.Page, query.Limit, filtered.Count);
    }
}
