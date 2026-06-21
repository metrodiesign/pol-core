using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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

    public ProductRepository(ProducerDbContext db) => _db = db;

    public void Add(Product product) => _db.Set<Product>().Add(product);

    public async Task<IReadOnlyList<Product>> ListByTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await _db.Set<Product>()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.CreatedAtUtc)
            .ToListAsync(cancellationToken);
}
