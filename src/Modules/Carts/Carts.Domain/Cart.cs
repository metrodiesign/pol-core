using Carts.Domain.Items;
using SharedKernel;

namespace Carts.Domain;

/// <summary>
/// A merchant's shopping cart aggregate: an ordered bag of <see cref="Items.Item"/> lines that all share
/// one currency. The cart is the transactional boundary — items are added, removed and cleared only
/// through it, and it is frozen (<see cref="CartStatus.CheckedOut"/>) once checkout begins. It holds
/// no money itself; the <see cref="Subtotal"/> is computed from its lines.
/// </summary>
public sealed class Cart : AggregateRoot<Guid>
{
    private readonly List<Item> _items = [];

    public Guid MerchantId { get; private set; }
    public CartStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }

    /// <summary>The cart's lines, in insertion order. Mutated only through the aggregate's methods.</summary>
    public IReadOnlyCollection<Item> Items => _items.AsReadOnly();

    /// <summary>Parameterless ctor for EF Core materialisation only.</summary>
    private Cart() { }

    public Cart(Guid id, Guid merchantId, DateTime createdAt)
        : base(id)
    {
        MerchantId = merchantId;
        Status = CartStatus.Open;
        CreatedAt = createdAt;
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
            i => i.ProductId == productId && i.UnitPrice.Amount == unitPrice.Amount);
        if (existing is not null)
        {
            existing.IncreaseQuantity(quantity);
            return;
        }

        _items.Add(new Item(Guid.CreateVersion7(), Id, productId, quantity, unitPrice));
    }

    /// <summary>Removes the line for <paramref name="productId"/>, if present.</summary>
    public void RemoveItem(Guid productId)
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");

        _items.RemoveAll(i => i.ProductId == productId);
    }

    /// <summary>Sets an existing line's quantity to a positive value. Rejects a non-open cart, a
    /// non-positive quantity, or a product that is not in the cart.</summary>
    public void SetItemQuantity(Guid productId, int quantity)
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");

        var line = _items.FirstOrDefault(i => i.ProductId == productId)
            ?? throw new ArgumentException($"Product {productId} is not in the cart.", nameof(productId));
        line.SetQuantity(quantity);
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

            var total = Money.Zero(_items[0].UnitPrice.Currency);
            foreach (var item in _items)
                total = total.Add(item.LineTotal);

            return total;
        }
    }

    private void EnsureCurrencyMatches(Money unitPrice)
    {
        if (_items.Count == 0)
            return;

        if (!string.Equals(_items[0].UnitPrice.Currency, unitPrice.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Currency mismatch: cart is {_items[0].UnitPrice.Currency}, item is {unitPrice.Currency}.");
    }
}
