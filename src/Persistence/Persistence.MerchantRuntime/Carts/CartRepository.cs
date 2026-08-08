using Carts.Application;
using Microsoft.EntityFrameworkCore;
using CartAggregate = Carts.Domain.Cart;

namespace Persistence.MerchantRuntime.Carts;

/// <summary>
/// Cart persistence over the MerchantRuntime data plane. Queries go through
/// <c>MerchantRuntimeDbContext.Set&lt;Cart&gt;()</c>; the query filter and the sealed write guard apply
/// merchant scoping, so this adapter only tracks and loads.
/// </summary>
internal sealed class CartRepository : ICartRepository, ICartForOrderStore
{
    private readonly MerchantRuntimeDbContext _db;

    public CartRepository(MerchantRuntimeDbContext db) => _db = db;

    public void Add(CartAggregate cart) => _db.Set<CartAggregate>().Add(cart);

    public Task<CartAggregate?> GetAsync(Guid cartId, CancellationToken cancellationToken) =>
        PlatformReadGuard.ReadAsync(ct => _db.Set<CartAggregate>()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == cartId, ct), cancellationToken);

    public Task<CartAggregate?> ReloadTrackedAsync(Guid cartId, CancellationToken cancellationToken)
    {
        // GetCartQuery earlier in the same HTTP scope tracks a snapshot. Detach only this aggregate graph so
        // transaction validation cannot accidentally reuse it after another request changed the cart.
        foreach (var entry in _db.ChangeTracker.Entries<global::Carts.Domain.Items.Item>()
                     .Where(e => e.Entity.CartId == cartId).ToArray())
            entry.State = EntityState.Detached;
        foreach (var entry in _db.ChangeTracker.Entries<CartAggregate>()
                     .Where(e => e.Entity.Id == cartId).ToArray())
            entry.State = EntityState.Detached;

        return GetAsync(cartId, cancellationToken);
    }
}
