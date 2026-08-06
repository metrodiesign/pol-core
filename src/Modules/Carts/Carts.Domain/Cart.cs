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

    /// <summary>
    /// Application-managed optimistic concurrency token (REQ-2.1). Every mutation bumps it — including
    /// item-only edits, which would otherwise never touch the cart's own row — so any two writers racing
    /// on the same cart (an item edit vs the checkout freeze) conflict at commit instead of silently
    /// interleaving. A SQL rowversion cannot do this job: it only moves when the Carts row itself is
    /// written, and it does not exist on the SQLite provider the host tests run on.
    /// </summary>
    public int Version { get; private set; }

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
    /// Adds <paramref name="quantity"/> of the insurance document <paramref name="documentNo"/> at
    /// <paramref name="unitPrice"/>. All lines must share one currency, so a mismatch is rejected.
    /// A document already in this cart is REJECTED rather than merged (products-external-source-of-truth
    /// REQ-9.4): one insurance document is sold once, so a second line for it could only ever fail later —
    /// at checkout, after the merchant had filled in a second insured person for it.
    /// </summary>
    public void AddItem(string documentNo, string saleCode, string productGroup, int quantity, Money unitPrice)
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");
        ArgumentException.ThrowIfNullOrWhiteSpace(documentNo, nameof(documentNo));

        // REQ-2.3 is the equality rule everywhere: trimmed, case ignored. Ordinal here is the C# fast-path
        // that must never be LOOSER than the column collation decides — inside one cart there is no SQL
        // comparison to disagree with, so the strict form is also the safe one.
        var wanted = documentNo.Trim();
        if (_items.Exists(i => string.Equals(i.DocumentNo, wanted, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The document is already in this cart.", nameof(documentNo));

        EnsureCurrencyMatches(unitPrice);
        Version++;

        _items.Add(new Item(
            Guid.CreateVersion7(), Id, MerchantId, wanted, saleCode, productGroup, quantity, unitPrice));
    }

    /// <summary>Removes the line <paramref name="itemId"/>. Returns false when the cart has no such line,
    /// which the caller turns into a 404 (REQ-9.3) — the id is a route segment, not a filter.</summary>
    public bool RemoveItem(Guid itemId)
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");

        if (_items.RemoveAll(i => i.Id == itemId) == 0)
            return false;

        Version++;
        return true;
    }

    /// <summary>Sets an existing line's quantity to a positive value. Rejects a non-open cart or a
    /// non-positive quantity; returns false when the cart has no line <paramref name="itemId"/> (REQ-9.3).</summary>
    public bool SetItemQuantity(Guid itemId, int quantity)
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity must be positive.");

        var line = _items.Find(i => i.Id == itemId);
        if (line is null)
            return false;

        Version++;
        line.SetQuantity(quantity);
        return true;
    }

    /// <summary>Empties the cart of all lines.</summary>
    public void Clear()
    {
        if (Status != CartStatus.Open)
            throw new InvalidOperationException("Cannot modify a cart that is not open.");

        Version++;
        _items.Clear();
    }

    /// <summary>Freezes the cart so it can no longer be edited. Bumps <see cref="Version"/> so an item
    /// edit that loaded the cart before the freeze landed is rejected at its own commit.</summary>
    public void MarkCheckedOut()
    {
        Status = CartStatus.CheckedOut;
        Version++;
    }

    /// <summary>Unfreezes a cart whose checkout was abandoned, so the merchant can edit it and check out
    /// again (REQ-2.5). An already-open cart is a no-op, which is what makes abandoning twice safe (REQ-2.9).</summary>
    public void Reopen()
    {
        if (Status == CartStatus.Open)
            return;

        Status = CartStatus.Open;
        Version++;
    }

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
