using CartAggregate = Carts.Domain.Cart;

namespace Carts.Application;

/// <summary>Narrow owner port used only by host-composed Cart-to-Order transaction.</summary>
public interface ICartForOrderStore
{
    /// <summary>Discards any earlier request snapshot and loads a fresh tracked aggregate inside transaction.</summary>
    Task<CartAggregate?> ReloadTrackedAsync(Guid cartId, CancellationToken cancellationToken);
}
