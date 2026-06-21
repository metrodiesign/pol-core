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
}
