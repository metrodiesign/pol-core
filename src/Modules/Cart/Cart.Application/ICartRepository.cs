using CartAggregate = Cart.Domain.Cart;

namespace Cart.Application;

/// <summary>
/// Persistence port for the cart aggregate. Implemented in Cart.Infrastructure over the shared
/// shop data plane; the application layer depends only on this abstraction. Adding/tracking is
/// flushed by the shared <c>IUnitOfWork</c>, so this port deliberately exposes no save method.
/// </summary>
public interface ICartRepository
{
    /// <summary>Tracks a new cart for insertion on the next unit-of-work commit.</summary>
    void Add(CartAggregate cart);

    /// <summary>Loads a cart with its items, or <c>null</c> if no cart with that id exists.</summary>
    Task<CartAggregate?> GetAsync(Guid cartId, CancellationToken cancellationToken);
}
