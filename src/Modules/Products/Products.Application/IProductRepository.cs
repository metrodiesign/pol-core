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

    /// <summary>Mirrors one page of upstream documents into the catalogue, keyed on <c>DocumentNo</c>: a
    /// document not seen before is created, one already here is refreshed
    /// (<see cref="Product.RefreshFromExternal"/>), and the whole page is saved in one unit of work. A
    /// concurrent request inserting the same <c>DocumentNo</c> (unique <c>IX_Products_DocumentNo</c>, SQL 2601/
    /// 2627 inside a <c>DbUpdateException</c>) is retried once against a reset change tracker — which is why
    /// the save lives down here rather than behind an Application-layer unit of work (M7). Returns the stored
    /// documents in the order of <paramref name="inputs"/>.</summary>
    Task<IReadOnlyList<Product>> UpsertByDocumentNoAsync(
        IReadOnlyList<ProductInput> inputs, CancellationToken cancellationToken);

    /// <summary>Loads one product by id (RLS scopes it to the bound merchant), or null if absent.</summary>
    Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken);
}
