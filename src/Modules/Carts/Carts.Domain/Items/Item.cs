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
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }

    /// <summary>The line total: unit price times quantity. Not mapped (EF ignores it).</summary>
    public Money LineTotal => Money.Of(UnitPrice.Amount * Quantity, UnitPrice.Currency);

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Item() { }

    internal Item(Guid id, Guid cartId, Guid productId, int quantity, Money unitPrice)
        : base(id)
    {
        CartId = cartId;
        ProductId = productId;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    internal void IncreaseQuantity(int by) => Quantity = checked(Quantity + by);

    internal void SetQuantity(int quantity) => Quantity = quantity;
}
