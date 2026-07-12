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

    [Fact]
    public void A_checked_out_cart_rejects_edits()
    {
        var cart = CartWithLine();
        cart.MarkCheckedOut();

        Assert.Throws<InvalidOperationException>(() => cart.SetItemQuantity(Product, 1));
        Assert.Throws<InvalidOperationException>(() => cart.RemoveItem(Product));
        Assert.Throws<InvalidOperationException>(() => cart.Clear());
    }
}
