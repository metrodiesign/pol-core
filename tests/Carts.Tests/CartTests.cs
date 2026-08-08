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
    private static readonly CommerceItemMetadata Metadata = new(
        CommerceItemMetadataCodec.InsuranceDocumentSource, "POLICY", "P-001", null, null);

    private static CartAggregate CartWithLine(int quantity = 2)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, SaleCode, Now);
        cart.AddItem(DocA, SaleCode, Group, "Motor", quantity, Money.Of(100m, "THB"), Metadata);
        return cart;
    }

    // products-external-source-of-truth REQ-9.4 — a document already in the cart is REJECTED, not merged: one
    // insurance document is sold once, so a second line for it could only fail later at checkout.
    [Fact]
    public void AddItem_rejects_a_duplicate_document()
    {
        var cart = CartWithLine(2);
        Assert.Throws<ArgumentException>(() =>
            cart.AddItem(DocA, SaleCode, Group, "Motor", 3, Money.Of(100m, "THB"), Metadata));

        Assert.Single(cart.Items);
        Assert.Equal(2, cart.Items.First().Quantity);
    }

    [Fact]
    public void AddItem_adds_a_second_distinct_document()
    {
        var cart = CartWithLine(2);
        cart.AddItem(DocB, SaleCode, Group, "Motor", 3, Money.Of(100m, "THB"), Metadata);

        Assert.Equal(2, cart.Items.Count);
        Assert.Equal(500m, cart.Subtotal!.Value.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddItem_rejects_non_positive_quantity(int quantity)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, SaleCode, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            cart.AddItem(DocA, SaleCode, Group, "Motor", quantity, Money.Of(100m, "THB"), Metadata));
        Assert.Empty(cart.Items);
    }

    [Fact]
    public void Rejected_source_snapshot_does_not_mutate_cart_version()
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, SaleCode, Now);

        Assert.Throws<ArgumentException>(() =>
            cart.AddItem(DocA, SaleCode, Group, new string('x', 129), 1, Money.Of(100m, "THB"), Metadata));

        Assert.Empty(cart.Items);
        Assert.Equal(0, cart.Version);
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

    // CheckedOut means direct Order creation consumed the Cart; all four mutations stay blocked.
    [Fact]
    public void A_checked_out_cart_rejects_edits()
    {
        var cart = CartWithLine();
        var itemId = cart.Items.First().Id;
        cart.MarkCheckedOut();

        Assert.Throws<InvalidOperationException>(() =>
            cart.AddItem(DocB, SaleCode, Group, "Motor", 1, Money.Of(100m, "THB"), Metadata));
        Assert.Throws<InvalidOperationException>(() => cart.SetItemQuantity(itemId, 1));
        Assert.Throws<InvalidOperationException>(() => cart.RemoveItem(itemId));
        Assert.Throws<InvalidOperationException>(() => cart.Clear());
    }

    // Every mutation must bump Version so optimistic concurrency can reject stale Cart-to-Order snapshots.
    [Fact]
    public void Every_mutation_bumps_the_version()
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, SaleCode, Now);
        Assert.Equal(0, cart.Version);

        cart.AddItem(DocA, SaleCode, Group, "Motor", 2, Money.Of(100m, "THB"), Metadata);
        Assert.Equal(1, cart.Version);
        var itemId = cart.Items.First().Id;
        cart.SetItemQuantity(itemId, 7);
        Assert.Equal(2, cart.Version);
        cart.RemoveItem(itemId);
        Assert.Equal(3, cart.Version);
        cart.AddItem(DocA, SaleCode, Group, "Motor", 1, Money.Of(100m, "THB"), Metadata); // re-add after remove
        cart.Clear();
        Assert.Equal(5, cart.Version);

        cart.MarkCheckedOut();
        Assert.Equal(6, cart.Version);
    }
}
