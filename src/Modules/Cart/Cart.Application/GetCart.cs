using BuildingBlocks.Application;
using Mediator;

namespace Cart.Application;

/// <summary>Reads a cart with its lines + subtotal. Tenant-scoped: RLS filters out a cart owned by
/// another tenant, so the query returns null for both "not found" and "not yours" (no existence leak).</summary>
public sealed record GetCartQuery(Guid CartId, Guid TenantId) : IQuery<CartView?>, ITenantScoped;

public sealed record CartLineView(Guid ProductId, int Quantity, long UnitPriceMinorUnits, string Currency, long LineTotalMinorUnits);

public sealed record CartView(
    Guid CartId, string Status, IReadOnlyList<CartLineView> Items, long? SubtotalMinorUnits, string? SubtotalCurrency)
{
    public static CartView From(Domain.Cart cart) => new(
        cart.Id,
        cart.Status.ToString(),
        cart.Items
            .Select(i => new CartLineView(i.ProductId, i.Quantity, i.UnitPriceMinorUnits, i.UnitPriceCurrency, i.LineTotal.MinorUnits))
            .ToList(),
        cart.Subtotal?.MinorUnits,
        cart.Subtotal?.Currency);
}

public sealed class GetCartHandler : IQueryHandler<GetCartQuery, CartView?>
{
    private readonly ICartRepository _carts;

    public GetCartHandler(ICartRepository carts) => _carts = carts;

    public async ValueTask<CartView?> Handle(GetCartQuery query, CancellationToken cancellationToken)
    {
        var cart = await _carts.GetAsync(query.CartId, cancellationToken).ConfigureAwait(false);
        if (cart is null || cart.TenantId != query.TenantId)
            return null;

        return CartView.From(cart);
    }
}
