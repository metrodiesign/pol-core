using BuildingBlocks.Application;
using Mediator;
using SharedKernel;
using System.Text.Json.Serialization;

namespace Carts.Application;

/// <summary>Reads a cart with its lines + subtotal. Merchant-scoped: RLS filters out a cart owned by
/// another merchant, so the query returns null for both "not found" and "not yours" (no existence leak).</summary>
public sealed record GetCartQuery(Guid CartId, Guid MerchantId) : IQuery<CartView?>, IMerchantScoped;

/// <summary>One cart line as the merchant sees it. <paramref name="ItemId"/> is the mutation handle;
/// product, variant, price and metadata are server-owned source snapshots.</summary>
public sealed record CartLineView(
    Guid ItemId,
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    Money UnitPrice,
    Money LineTotal,
    System.Text.Json.JsonElement? Metadata);

public sealed record CartView(
    Guid CartId, string? SaleCode, string Status, IReadOnlyList<CartLineView> Items, Money? Subtotal, int Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? OriginatorId = null)
{
    public static CartView From(Domain.Cart cart) => new(
        cart.Id,
        cart.SaleCode,
        cart.Status.ToString(),
        cart.Items
            .Select(i => new CartLineView(
                i.Id, i.ProductCode, i.VariantCode, i.VariantName, i.Quantity, i.UnitPrice, i.LineTotal,
                i.Metadata is null ? null : CommerceItemMetadataCodec.ToJsonElement(i.Metadata)))
            .ToList(),
        cart.Subtotal,
        cart.Version,
        cart.OriginatorId);
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
