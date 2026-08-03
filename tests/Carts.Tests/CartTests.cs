using SharedKernel;
using CartAggregate = Carts.Domain.Cart;

namespace Carts.Tests;

public sealed class CartTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    private static CartAggregate CartWithLine(int quantity = 2)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, Now);
        cart.AddItem(Product, quantity, Money.Of(100m, "THB"));
        return cart;
    }

    [Fact]
    public void AddItem_merges_same_product_and_subtotal_sums()
    {
        var cart = CartWithLine(2);
        cart.AddItem(Product, 3, Money.Of(100m, "THB")); // merges

        Assert.Single(cart.Items);
        Assert.Equal(5, cart.Items.First().Quantity);
        Assert.Equal(500m, cart.Subtotal!.Value.Amount);
    }

    [Fact]
    public void SetItemQuantity_sets_an_existing_line()
    {
        var cart = CartWithLine(2);
        cart.SetItemQuantity(Product, 7);
        Assert.Equal(7, cart.Items.First().Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetItemQuantity_rejects_a_non_positive_quantity(int quantity)
    {
        var cart = CartWithLine();
        Assert.Throws<ArgumentOutOfRangeException>(() => cart.SetItemQuantity(Product, quantity));
    }

    [Fact]
    public void SetItemQuantity_rejects_a_product_not_in_the_cart()
    {
        var cart = CartWithLine();
        Assert.Throws<ArgumentException>(() => cart.SetItemQuantity(Guid.NewGuid(), 1));
    }

    [Fact]
    public void RemoveItem_drops_the_line()
    {
        var cart = CartWithLine();
        cart.RemoveItem(Product);
        Assert.Empty(cart.Items);
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
        cart.MarkCheckedOut();

        Assert.Throws<InvalidOperationException>(() => cart.AddItem(Guid.NewGuid(), 1, Money.Of(100m, "THB")));
        Assert.Throws<InvalidOperationException>(() => cart.SetItemQuantity(Product, 1));
        Assert.Throws<InvalidOperationException>(() => cart.RemoveItem(Product));
        Assert.Throws<InvalidOperationException>(() => cart.Clear());
    }

    // REQ-2.5 — abandoning the checkout hands the cart back, edits and all.
    [Fact]
    public void Reopen_unfreezes_a_checked_out_cart()
    {
        var cart = CartWithLine();
        cart.MarkCheckedOut();

        cart.Reopen();

        cart.SetItemQuantity(Product, 4);
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

        cart.AddItem(Product, 2, Money.Of(100m, "THB"));
        Assert.Equal(1, cart.Version);
        cart.SetItemQuantity(Product, 7);
        Assert.Equal(2, cart.Version);
        cart.RemoveItem(Product);
        Assert.Equal(3, cart.Version);
        cart.AddItem(Product, 1, Money.Of(100m, "THB"));
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
