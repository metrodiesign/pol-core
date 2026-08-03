using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Carts.Application;

/// <summary>Reads a cart with its lines + subtotal. Merchant-scoped: RLS filters out a cart owned by
/// another merchant, so the query returns null for both "not found" and "not yours" (no existence leak).</summary>
public sealed record GetCartQuery(Guid CartId, Guid MerchantId) : IQuery<CartView?>, IMerchantScoped;

public sealed record CartLineView(Guid ProductId, int Quantity, Money UnitPrice, Money LineTotal);

public sealed record CartView(
    Guid CartId, string Status, IReadOnlyList<CartLineView> Items, Money? Subtotal, int Version)
{
    public static CartView From(Domain.Cart cart) => new(
        cart.Id,
        cart.Status.ToString(),
        cart.Items
            .Select(i => new CartLineView(i.ProductId, i.Quantity, i.UnitPrice, i.LineTotal))
            .ToList(),
        cart.Subtotal,
        cart.Version);
}

public sealed class GetCartHandler : IQueryHandler<GetCartQuery, CartView?>
{
    private readonly ICartRepository _carts;

    public GetCartHandler(ICartRepository carts) => _carts = carts;

    public async ValueTask<CartView?> Handle(GetCartQuery query, CancellationToken cancellationToken)
    {
        var cart = await _carts.GetAsync(query.CartId, cancellationToken).ConfigureAwait(false);
        if (cart is null || cart.MerchantId != query.MerchantId)
            return null;

        return CartView.From(cart);
    }
}
