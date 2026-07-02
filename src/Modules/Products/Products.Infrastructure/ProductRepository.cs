using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Products.Application;
using Products.Domain;

namespace Products.Infrastructure;

/// <summary>
/// Binds <see cref="IProductRepository"/> to the shared <c>producer</c> data plane via
/// <c>ProducerDbContext.Set&lt;Product&gt;()</c>. Scoped (depends on the Scoped DbContext).
/// </summary>
public sealed class ProductRepository : IProductRepository
{
    private readonly ProducerDbContext _db;
    private readonly ILogger<ProductRepository> _logger;

    public ProductRepository(ProducerDbContext db, ILogger<ProductRepository> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void Add(Product product) => _db.Set<Product>().Add(product);

    public async Task<IReadOnlyList<Product>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _db.Set<Product>()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

    public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        _db.Set<Product>().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    public async Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery query, CancellationToken cancellationToken)
    {
        IQueryable<Product> src = _db.Set<Product>().AsNoTracking()
            .Where(p => p.TenantId == query.TenantId)   // defence-in-depth on the SQL RLS floor (REQ-7.1)
            .ApplySearch(query.Search)
            .ApplyFilters(query.Filters, _logger);

        if (query.ProductFilters is { } pf)   // typed strict filter (REQ-10)
        {
            if (pf.MinPriceMinorUnits is { } min) src = src.Where(p => p.PriceMinorUnits >= min);
            if (pf.MaxPriceMinorUnits is { } max) src = src.Where(p => p.PriceMinorUnits <= max);
            if (pf.ActiveOnly == true) src = src.Where(p => p.IsActive);
        }

        long total = await src.LongCountAsync(cancellationToken);   // after filter/search, before paging (REQ-2.5)

        int skip = (int)Math.Min((long)(query.Page - 1) * query.Limit, int.MaxValue);   // overflow-safe offset (REQ-2.6)

        // Project SCALAR columns only — never p.Price (unmapped computed Money; D15).
        var items = await src
            .ApplySort(query.Sort, _logger)
            .Skip(skip)
            .Take(query.Limit)
            .Select(p => new ProductListItem(
                p.Id, p.TenantId, p.Name, p.PriceMinorUnits, p.PriceCurrency, p.IsActive, p.CreatedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<ProductListItem>(items, query.Page, query.Limit, total);
    }
}
