using SharedKernel;

namespace Cart.Domain;

/// <summary>
/// A line in a <see cref="Cart"/>. Owned by its cart aggregate — never loaded or mutated on its own.
/// The unit price is stored as two scalar columns (minor units + ISO 4217 code) per the EF money
/// mapping rule and re-projected into a validating <see cref="Money"/> via <see cref="UnitPrice"/>.
/// </summary>
public sealed class CartItem : Entity<Guid>
{
    public Guid CartId { get; private set; }
    public Guid ProductId { get; private set; }
    public int Quantity { get; private set; }
    public long UnitPriceMinorUnits { get; private set; }
    public string UnitPriceCurrency { get; private set; } = default!;

    /// <summary>The unit price re-projected from its scalar columns; not mapped (EF ignores it).</summary>
    public Money UnitPrice => Money.Of(UnitPriceMinorUnits, UnitPriceCurrency);

    /// <summary>The line total: unit price times quantity.</summary>
    public Money LineTotal => Money.Of(checked(UnitPriceMinorUnits * Quantity), UnitPriceCurrency);

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private CartItem() { }

    internal CartItem(Guid id, Guid cartId, Guid productId, int quantity, Money unitPrice)
        : base(id)
    {
        CartId = cartId;
        ProductId = productId;
        Quantity = quantity;
        UnitPriceMinorUnits = unitPrice.MinorUnits;
        UnitPriceCurrency = unitPrice.Currency;
    }

    internal void IncreaseQuantity(int by) => Quantity = checked(Quantity + by);
}
