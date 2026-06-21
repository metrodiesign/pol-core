using SharedKernel;

namespace Cart.Domain;

/// <summary>
/// A tenant's shopping cart aggregate: an ordered bag of <see cref="CartItem"/> lines that all share
/// one currency. The cart is the transactional boundary — items are added, removed and cleared only
/// through it, and it is frozen (<see cref="CartStatus.CheckedOut"/>) once checkout begins. It holds
/// no money itself; the <see cref="Subtotal"/> is computed from its lines.
/// </summary>
public sealed class Cart : AggregateRoot<Guid>
{
    private readonly List<CartItem> _items = [];

    public Guid TenantId { get; private set; }
    public CartStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    /// <summary>The cart's lines, in insertion order. Mutated only through the aggregate's methods.</summary>
    public IReadOnlyCollection<CartItem> Items => _items.AsReadOnly();

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Cart() { }

    public Cart(Guid id, Guid tenantId, DateTime createdAtUtc)
        : base(id)
    {
        TenantId = tenantId;
        Status = CartStatus.Open;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Adds <paramref name="quantity"/> of a product at <paramref name="unitPrice"/>. All lines must
    /// share one currency, so a mismatch is rejected. Re-adding the same product at the same unit
    /// price merges into the existing line rather than creating a duplicate.
    /// </summary>
    public void AddItem(Guid productId, int quantity, Money unitPrice)
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");

        EnsureCurrencyMatches(unitPrice);

        var existing = _items.FirstOrDefault(
            i => i.ProductId == productId && i.UnitPriceMinorUnits == unitPrice.MinorUnits);
        if (existing is not null)
        {
            existing.IncreaseQuantity(quantity);
            return;
        }

        _items.Add(new CartItem(Guid.CreateVersion7(), Id, productId, quantity, unitPrice));
    }

    /// <summary>Removes the line for <paramref name="productId"/>, if present.</summary>
    public void RemoveItem(Guid productId)
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");

        _items.RemoveAll(i => i.ProductId == productId);
    }

    /// <summary>Empties the cart of all lines.</summary>
    public void Clear()
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");

        _items.Clear();
    }

    /// <summary>Freezes the cart so it can no longer be edited.</summary>
    public void MarkCheckedOut() => Status = CartStatus.CheckedOut;

    /// <summary>
    /// The sum of every line total. An empty cart has no currency to denominate zero in, so callers
    /// must guard against that — this returns <c>null</c> when the cart is empty.
    /// </summary>
    public Money? Subtotal
    {
        get
        {
            if (_items.Count == 0)
                return null;

            var total = Money.Zero(_items[0].UnitPriceCurrency);
            foreach (var item in _items)
                total = total.Add(item.LineTotal);

            return total;
        }
    }

    private void EnsureCurrencyMatches(Money unitPrice)
    {
        if (_items.Count == 0)
            return;

        if (!string.Equals(_items[0].UnitPriceCurrency, unitPrice.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Currency mismatch: cart is {_items[0].UnitPriceCurrency}, item is {unitPrice.Currency}.");
    }
}
