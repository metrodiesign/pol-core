using BuildingBlocks.Infrastructure.Persistence;
using Cart.Application;
using Microsoft.EntityFrameworkCore;
using CartAggregate = Cart.Domain.Cart;

namespace Cart.Infrastructure;

/// <summary>
/// Cart persistence over the shared shop data plane. Queries go through
/// <c>PolDbContext.Set&lt;Cart&gt;()</c>; the RLS interceptor and the shared unit of work apply
/// merchant scoping and commit, so this adapter only tracks and loads.
/// </summary>
public sealed class CartRepository : ICartRepository
{
    private readonly PolDbContext _db;

    public CartRepository(PolDbContext db) => _db = db;

    public void Add(CartAggregate cart) => _db.Set<CartAggregate>().Add(cart);

    public Task<CartAggregate?> GetAsync(Guid cartId, CancellationToken cancellationToken) =>
        _db.Set<CartAggregate>()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == cartId, cancellationToken);
}
