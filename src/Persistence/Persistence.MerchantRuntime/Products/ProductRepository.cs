using Microsoft.Data.SqlClient;
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
    private readonly MerchantRuntimeDbContext _db;

    public ProductRepository(MerchantRuntimeDbContext db) => _db = db;

    public void Add(Product product) => _db.Set<Product>().Add(product);

    public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
        _db.Set<Product>().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);

    public async Task<IReadOnlyList<Product>> UpsertByDocumentNoAsync(
        IReadOnlyList<ProductInput> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) return [];

        try
        {
            return await ApplyAsync(inputs, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another request created one of these documents between our read and our save. Every entity we
            // staged is now suspect — the Added one is still Added and would collide again — so the tracked
            // graph goes and the whole page is replayed against what is in the table now.
            // ponytail: one retry; a page is at most 25 rows and losing the same race twice is not a case
            // worth designing for. Losing it twice throws, which is the honest answer for a write.
            _db.ChangeTracker.Clear();
            return await ApplyAsync(inputs, cancellationToken);
        }
    }

    /// <summary>One pass of the upsert. A <see cref="Product.RefreshFromExternal"/> that throws part-way
    /// leaves its aggregate partly applied on purpose (the guards it fails on mean the upstream sent a row
    /// for the wrong document): the exception has to travel out with nothing saved, so this method never
    /// catches one and saves the rest.</summary>
    private async Task<IReadOnlyList<Product>> ApplyAsync(
        IReadOnlyList<ProductInput> inputs, CancellationToken cancellationToken)
    {
        // Tracked on purpose: RefreshFromExternal mutates these, and EF writes only what actually changed —
        // which is also what keeps a concurrent MarkPaid from being overwritten by an unchanged value (F3).
        var documentNos = inputs.Select(i => i.DocumentNo).ToArray();
        var stored = await _db.Set<Product>()
            .Where(p => documentNos.Contains(p.DocumentNo))
            .ToDictionaryAsync(p => p.DocumentNo, cancellationToken);

        var documents = new List<Product>(inputs.Count);
        foreach (var input in inputs)
        {
            if (stored.TryGetValue(input.DocumentNo, out var document))
            {
                document.RefreshFromExternal(input);
            }
            else
            {
                document = Product.Create(input);
                _db.Set<Product>().Add(document);
                stored[input.DocumentNo] = document;
            }

            documents.Add(document);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return documents;
    }

    // SQL Server: 2627 = unique constraint, 2601 = duplicate key in a unique index. Read off the inner
    // exception rather than by opening a connection of our own, which this assembly must never do.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 or 2601 };
}
