using SharedKernel;

namespace Carts.Domain.Items;

/// <summary>
/// A line in a <see cref="Domain.Cart"/>. Owned by its cart aggregate — never loaded or mutated on its own.
/// The unit price is mapped as an EF complex type (rf1 — decimal(19,4) + char(3) columns), per the
/// EF money mapping rule.
/// </summary>
public sealed class Item : Entity<Guid>
{
    public Guid CartId { get; private set; }

    /// <summary>Denormalized from the parent <see cref="Domain.Cart"/> at construction (rls-to-query-filter
    /// REQ-6) — Item has no navigation to Cart, so this is its own tenant key for the read floor. Enforced
    /// against drift by a composite FK <c>(CartId, MerchantId) → Cart(Id, MerchantId)</c>; only
    /// <see cref="Domain.Cart.AddItem"/> stamps it, so it can never diverge from the parent in practice.</summary>
    public Guid MerchantId { get; private set; }

    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }

    /// <summary>The line total: unit price times quantity. Not mapped (EF ignores it).</summary>
    public Money LineTotal => Money.Of(UnitPrice.Amount * Quantity, UnitPrice.Currency);

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Item() { }

    internal Item(Guid id, Guid cartId, Guid merchantId, Guid productId, int quantity, Money unitPrice)
        : base(id)
    {
        CartId = cartId;
        MerchantId = merchantId;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    internal void IncreaseQuantity(int by) => Quantity = checked(Quantity + by);

    internal void SetQuantity(int quantity) => Quantity = quantity;
}
