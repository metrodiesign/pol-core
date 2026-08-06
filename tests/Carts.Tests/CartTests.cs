using SharedKernel;
using CartAggregate = Carts.Domain.Cart;

namespace Carts.Tests;

public sealed class CartTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.NewGuid();
    private const string DocA = "00098-69100/กธ/900001-10";
    private const string DocB = "00098-69100/กธ/900002-10";
    private const string SaleCode = "77001";
    private const string Group = "VMI";

    private static CartAggregate CartWithLine(int quantity = 2)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, Now);
        cart.AddItem(DocA, SaleCode, Group, quantity, Money.Of(100m, "THB"));
        return cart;
    }

    // products-external-source-of-truth REQ-9.4 — a document already in the cart is REJECTED, not merged: one
    // insurance document is sold once, so a second line for it could only fail later at checkout.
    [Fact]
    public void AddItem_rejects_a_duplicate_document()
    {
        var cart = CartWithLine(2);
        Assert.Throws<ArgumentException>(() => cart.AddItem(DocA, SaleCode, Group, 3, Money.Of(100m, "THB")));

        Assert.Single(cart.Items);
        Assert.Equal(2, cart.Items.First().Quantity);
    }

    [Fact]
    public void AddItem_adds_a_second_distinct_document()
    {
        var cart = CartWithLine(2);
        cart.AddItem(DocB, SaleCode, Group, 3, Money.Of(100m, "THB"));

        Assert.Equal(2, cart.Items.Count);
        Assert.Equal(500m, cart.Subtotal!.Value.Amount);
    }

    [Fact]
    public void SetItemQuantity_sets_an_existing_line()
    {
        var cart = CartWithLine(2);
        var itemId = cart.Items.First().Id;
        Assert.True(cart.SetItemQuantity(itemId, 7));
        Assert.Equal(7, cart.Items.First().Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetItemQuantity_rejects_a_non_positive_quantity(int quantity)
    {
        var cart = CartWithLine();
        var itemId = cart.Items.First().Id;
        Assert.Throws<ArgumentOutOfRangeException>(() => cart.SetItemQuantity(itemId, quantity));
    }

    // REQ-9.3 — an item not in the cart returns false (the caller turns that into a 404), it does not throw.
    [Fact]
    public void SetItemQuantity_returns_false_for_an_item_not_in_the_cart()
    {
        var cart = CartWithLine();
        Assert.False(cart.SetItemQuantity(Guid.NewGuid(), 1));
    }

    [Fact]
    public void RemoveItem_drops_the_line()
    {
        var cart = CartWithLine();
        var itemId = cart.Items.First().Id;
        Assert.True(cart.RemoveItem(itemId));
        Assert.Empty(cart.Items);
    }

    [Fact]
    public void RemoveItem_returns_false_for_an_item_not_in_the_cart()
    {
        var cart = CartWithLine();
        Assert.False(cart.RemoveItem(Guid.NewGuid()));
    }

    [Fact]
    public void Clear_empties_the_cart()
    {
        var cart = CartWithLine();
        cart.Clear();
        Assert.Empty(cart.Items);
    }

    // REQ-2.7 — all FOUR mutations, not three: add-item is the one that would otherwise let a merchant grow a
    // cart whose price snapshot is already frozen inside a live checkout session.
    [Fact]
    public void A_checked_out_cart_rejects_edits()
    {
        var cart = CartWithLine();
        var itemId = cart.Items.First().Id;
        cart.MarkCheckedOut();

        Assert.Throws<InvalidOperationException>(() => cart.AddItem(DocB, SaleCode, Group, 1, Money.Of(100m, "THB")));
        Assert.Throws<InvalidOperationException>(() => cart.SetItemQuantity(itemId, 1));
        Assert.Throws<InvalidOperationException>(() => cart.RemoveItem(itemId));
        Assert.Throws<InvalidOperationException>(() => cart.Clear());
    }

    // REQ-2.5 — abandoning the checkout hands the cart back, edits and all.
    [Fact]
    public void Reopen_unfreezes_a_checked_out_cart()
    {
        var cart = CartWithLine();
        var itemId = cart.Items.First().Id;
        cart.MarkCheckedOut();

        cart.Reopen();

        cart.SetItemQuantity(itemId, 4);
        Assert.Equal(4, cart.Items.First().Quantity);
    }

    // REQ-2.9 — abandoning twice must not fail, which is only true if reopening an open cart is a no-op.
    [Fact]
    public void Reopen_on_an_open_cart_changes_nothing()
    {
        var cart = CartWithLine(2);

        cart.Reopen();
        cart.Reopen();

        Assert.Single(cart.Items);
        Assert.Equal(2, cart.Items.First().Quantity);
        cart.Clear(); // still open for edits
    }

    // PR #166 review — the freeze-race token: EVERY mutation must bump Version (item edits included, since
    // they never touch the Carts row on their own), or a writer racing the checkout freeze slips through
    // the WHERE Version = @original check unnoticed.
    [Fact]
    public void Every_mutation_bumps_the_version_and_a_reopen_noop_does_not()
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, Now);
        Assert.Equal(0, cart.Version);

        cart.AddItem(DocA, SaleCode, Group, 2, Money.Of(100m, "THB"));
        Assert.Equal(1, cart.Version);
        var itemId = cart.Items.First().Id;
        cart.SetItemQuantity(itemId, 7);
        Assert.Equal(2, cart.Version);
        cart.RemoveItem(itemId);
        Assert.Equal(3, cart.Version);
        cart.AddItem(DocA, SaleCode, Group, 1, Money.Of(100m, "THB")); // re-add after remove — no longer a duplicate
        cart.Clear();
        Assert.Equal(5, cart.Version);

        cart.MarkCheckedOut();
        Assert.Equal(6, cart.Version);
        cart.Reopen();
        Assert.Equal(7, cart.Version);
        cart.Reopen(); // no-op on an open cart (REQ-2.9) — no write, no bump
        Assert.Equal(7, cart.Version);
    }
}
