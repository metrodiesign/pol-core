using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Products.Application;
using Products.Domain;

namespace Persistence.MerchantRuntime.Products;

/// <summary>
/// Binds <see cref="IProductRepository"/> to the MerchantRuntime data plane via
/// <c>MerchantRuntimeDbContext.Set&lt;Product&gt;()</c>. Scoped (depends on the Scoped DbContext).
/// </summary>
internal sealed class ProductRepository : IProductRepository
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly ILogger<ProductRepository> _logger;

    public ProductRepository(MerchantRuntimeDbContext db, ILogger<ProductRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Add(Product product) => _db.Set<Product>().Add(product);

    public async Task<IReadOnlyList<Product>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await _db.Set<Product>()
            .Where(p => p.MerchantId == merchantId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        _db.Set<Product>().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    public async Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery query, CancellationToken cancellationToken)
    {
        IQueryable<Product> src = _db.Set<Product>().AsNoTracking()
            .Where(p => p.MerchantId == query.MerchantId)   // defence-in-depth on the SQL RLS floor (REQ-7.1)
            .ApplySearch(query.Search)
            .ApplyFilters(query.Filters, _logger);

        if (query.ProductFilters is { } pf)   // typed strict filter mirroring SP guide §2 (null enum = ALL)
        {
            if (pf.SearchText is { } text && !string.IsNullOrWhiteSpace(text))
            {
                var pattern = $"%{SfsLike.Escape(text.Trim())}%";
                src = src.Where(p =>
                    EF.Functions.Like(p.DocumentNo, pattern, "\\")
                    || (p.PolicyNumber != null && EF.Functions.Like(p.PolicyNumber, pattern, "\\"))
                    || (p.ApplicationNumber != null && EF.Functions.Like(p.ApplicationNumber, pattern, "\\"))
                    || (p.EndorsementNumber != null && EF.Functions.Like(p.EndorsementNumber, pattern, "\\"))
                    || (p.LicensePlateNumber != null && EF.Functions.Like(p.LicensePlateNumber, pattern, "\\")));
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
            if (pf.PaymentStatus is { } paymentStatus) src = src.Where(p => p.PaymentStatus == paymentStatus);

            // Coverage bounds are dates (inclusive, SP guide §2) over datetime2(0) columns -> half-open upper.
            if (pf.CoverageStartFrom is { } csf) { var v = csf.ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.StartDate >= v); }
            if (pf.CoverageStartTo is { } cst) { var v = cst.AddDays(1).ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.StartDate < v); }
            if (pf.CoverageEndFrom is { } cef) { var v = cef.ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.EndDate >= v); }
            if (pf.CoverageEndTo is { } cet) { var v = cet.AddDays(1).ToDateTime(TimeOnly.MinValue); src = src.Where(p => p.EndDate < v); }
            if (pf.PaidDateFrom is { } pdf) src = src.Where(p => p.PaidDate >= pdf);
            if (pf.PaidDateTo is { } pdt) src = src.Where(p => p.PaidDate <= pdt);
        }

        long total = await src.LongCountAsync(cancellationToken);   // after filter/search, before paging (REQ-2.5)

        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);   // overflow-safe offset (REQ-2.6)

        var items = await src
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Select(p => new ProductListItem(
                p.Id, p.MerchantId, p.DocumentNo, p.DocumentType, p.ProductGroup, p.ShowName, p.PolicyNumber,
                p.StartDate, p.EndDate, p.TotalPremium, p.PaymentStatus, p.PaidDate, p.IsActive, p.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<ProductListItem>(items, query.Page, query.Limit, total);
    }
}
