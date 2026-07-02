using BuildingBlocks.Application;
using Products.Domain;

namespace Products.Application;

/// <summary>
/// Persistence port for the <see cref="Product"/> aggregate. The infrastructure adapter binds this
/// to the shared <c>producer</c> data plane; tenant isolation is enforced by the RLS floor, so
/// callers pass the tenant explicitly and never cross-tenant query.
/// </summary>
public interface IProductRepository
{
    /// <summary>Stages a new product for insertion; persisted by the unit of work's SaveChanges.</summary>
    void Add(Product product);

    /// <summary>Lists a tenant's products, newest first.</summary>
    Task<IReadOnlyList<Product>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <summary>The SFS-paged product list (REQ-2, REQ-7): tenant-scoped filter/search/sort over the RLS floor,
    /// projected to scalar <see cref="ProductListItem"/>s, with <c>Total</c> counted after filter/search but
    /// before paging.</summary>
    Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery query, CancellationToken cancellationToken);

    /// <summary>Loads one product by id (RLS scopes it to the bound tenant), or null if absent.</summary>
    Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken);
}
