using BuildingBlocks.Application;
using Products.Domain;

namespace Products.Application;

/// <summary>
/// Persistence port for the <see cref="Product"/> aggregate. The infrastructure adapter binds this
/// to the shared <c>shop</c> data plane; merchant isolation is enforced by the RLS floor, so
/// callers pass the merchant explicitly and never cross-merchant query.
/// </summary>
public interface IProductRepository
{
    /// <summary>Stages a new product for insertion; persisted by the unit of work's SaveChanges.</summary>
    void Add(Product product);

    /// <summary>The paged document list (§2 input surface): merchant-scoped filtering over the query-filter
    /// floor, ordered by <c>DocumentNo</c> and projected to <see cref="ProductListItem"/>s, with
    /// <c>Total</c> counted after filtering but before paging.</summary>
    Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery query, CancellationToken cancellationToken);

    /// <summary>Loads one product by id (RLS scopes it to the bound merchant), or null if absent.</summary>
    Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken);
}
