using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Products.Application;
using Products.Domain;

namespace Persistence.MerchantRuntime.Products;

/// <summary>
/// Binds <see cref="IProductRepository"/> to the MerchantRuntime data plane via
/// <c>MerchantRuntimeDbContext.Set&lt;Product&gt;()</c>. Scoped (depends on the Scoped DbContext).
/// </summary>
internal sealed class ProductRepository : IProductRepository
{
    // §3/§4 search window: general documents look back SearchWindowMonths on StartDate; RENEWAL looks
    // forward RenewalWindowMonths on EndDate. Named + in one place so the window can be retuned (or the
    // RENEWAL direction flipped) in a single line once the SP owner confirms it (REQ-6.4).
    private const int SearchWindowMonths = 6;
    private const int RenewalWindowMonths = 2;

    private readonly MerchantRuntimeDbContext _db;
    private readonly IClock _clock;

    public ProductRepository(MerchantRuntimeDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Add(Product product) => _db.Set<Product>().Add(product);

    public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        _db.Set<Product>().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    public async Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery query, CancellationToken cancellationToken)
    {
        var pf = query.ProductFilters;

        IQueryable<Product> src = _db.Set<Product>().AsNoTracking()
            .Where(p => p.MerchantId == query.MerchantId)   // defence-in-depth on the query-filter floor (REQ-7.1)
            .Where(p => p.SaleCode == pf.SaleCode);         // §2 @SaleCode — required (REQ-3.3)

        // §3 RENEWAL is read as forward-looking: EndDate (= period_to) within [today, today + 2 months) means
        // "about to expire, ready to renew". The document only says "window 2 เดือนตาม period_to / p_to"
        // without a direction — flipping it is a one-line change to the clause below (REQ-6.2/6.4).
        // Accepted side effect: rows with a NULL StartDate/EndDate fall outside the window (SQL `>=` on NULL).
        var today = _clock.UtcNow.Date;
        src = src.Where(p =>
            (p.DocumentType == DocumentType.RENEWAL
                && p.EndDate >= today && p.EndDate < today.AddMonths(RenewalWindowMonths))
            || (p.DocumentType != DocumentType.RENEWAL
                && p.StartDate >= today.AddMonths(-SearchWindowMonths)));

        if (pf.PaymentStatusFilter is { } paymentStatus)   // null = wire ALL = do not filter (REQ-3.1/3.2)
            src = src.Where(p => p.PaymentStatus == paymentStatus);

        if (pf.SearchText is { } text && !string.IsNullOrWhiteSpace(text))
        {
            var pattern = $"%{SfsLike.Escape(text.Trim())}%";
            // The licence plate joins the predicate only on Motor rows (§3 vs §4). The gate is per-row, not
            // per-request, so it stays correct when the client sends no productGroup; Product.InsuranceType is
            // builder.Ignore and cannot be translated, hence the explicit enum comparison (REQ-5.1/5.2).
            src = src.Where(p =>
                EF.Functions.Like(p.DocumentNo, pattern, "\\")
                || (p.PolicyNumber != null && EF.Functions.Like(p.PolicyNumber, pattern, "\\"))
                || (p.ApplicationNumber != null && EF.Functions.Like(p.ApplicationNumber, pattern, "\\"))
                || (p.EndorsementNumber != null && EF.Functions.Like(p.EndorsementNumber, pattern, "\\"))
                || ((p.ProductGroup == ProductGroup.CMI || p.ProductGroup == ProductGroup.VMI)
                    && p.LicensePlateNumber != null && EF.Functions.Like(p.LicensePlateNumber, pattern, "\\")));
        }
        if (pf.InsuredName is { } insured && !string.IsNullOrWhiteSpace(insured))
        {
            var pattern = $"%{SfsLike.Escape(insured.Trim())}%";
            src = src.Where(p => p.ShowName != null && EF.Functions.Like(p.ShowName, pattern, "\\"));
        }
        if (pf.PolicyNo is { } policyNo) src = src.Where(p => p.PolicyNumber == policyNo);
        if (pf.ApplicationNo is { } applicationNo) src = src.Where(p => p.ApplicationNumber == applicationNo);
        if (pf.DocumentType is { } documentType) src = src.Where(p => p.DocumentType == documentType);
        if (pf.ProductGroup is { } productGroup) src = src.Where(p => p.ProductGroup == productGroup);

        // Coverage bounds are dates (inclusive, SP guide §2) over datetime2(0) columns -> half-open upper.
        if (pf.CoverageStartFrom is { } csf) { var v = csf.ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.StartDate >= v); }
        if (pf.CoverageStartTo is { } cst) { var v = cst.AddDays(1).ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.StartDate < v); }
        if (pf.CoverageEndFrom is { } cef) { var v = cef.ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.EndDate >= v); }
        if (pf.CoverageEndTo is { } cet) { var v = cet.AddDays(1).ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.EndDate < v); }
        if (pf.PaidDateFrom is { } pdf) src = src.Where(p => p.PaidDate >= pdf);
        if (pf.PaidDateTo is { } pdt) src = src.Where(p => p.PaidDate <= pdt);

        long total = await src.LongCountAsync(cancellationToken);   // after filter/search, before paging (REQ-2.5)

        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);   // overflow-safe offset (REQ-2.6)

        var items = await src
            .OrderBy(p => p.DocumentNo)   // unique per merchant -> stable paging without a tie-breaker (REQ-7.2)
            .Skip(skip)
            .Take(query.Limit)
            .Select(p => new ProductListItem(
                p.Id, p.ProductGroup, p.DocumentType, p.DocumentNo, p.PolicyYear,
                p.ReferenceBranch, p.ReferencePre, p.PolicySequenceNo, p.ReferenceYear, p.ReferenceNo,
                p.PolicyBranch, p.PolicyType, p.SaleCode, p.SaleFullName, p.BrokerCode, p.BrokerName,
                p.PolicyNumber, p.ApplicationNumber, p.PreviousPolicyNumber, p.EndorsementNumber,
                p.StartDate, p.EndDate, p.ShowName,
                p.NetPremium, p.Stamp, p.TaxVat, p.TotalPremium, p.CommissionPercent, p.CommissionAmount,
                p.PaidDate, p.LicensePlateNumber, p.PaymentStatus))
            .ToListAsync(cancellationToken);

        return new PagedResult<ProductListItem>(items, query.Page, query.Limit, total);
    }
}
