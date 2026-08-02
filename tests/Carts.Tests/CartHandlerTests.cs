using Carts.Application;
using SharedKernel;
using CartAggregate = Carts.Domain.Cart;

namespace Carts.Tests;

public sealed class CartHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    private static CartAggregate SeededCart(out Guid cartId)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, Now);
        cart.AddItem(Product, 2, Money.Of(150m, "THB"));
        cartId = cart.Id;
        return cart;
    }

    [Fact]
    public async Task GetCart_returns_the_view_for_its_own_tenant()
    {
        var cart = SeededCart(out var cartId);
        var handler = new GetCartHandler(new FakeCartRepository(cart));

        var view = await handler.Handle(new GetCartQuery(cartId, Merchant), default);

        Assert.NotNull(view);
        Assert.Single(view!.Items);
        Assert.Equal(300m, view.Subtotal!.Value.Amount);
        Assert.Equal("THB", view.Subtotal.Value.Currency);
    }

    [Fact]
    public async Task GetCart_returns_null_for_another_tenant_or_missing()
    {
        var cart = SeededCart(out var cartId);
        var handler = new GetCartHandler(new FakeCartRepository(cart));

        Assert.Null(await handler.Handle(new GetCartQuery(cartId, Guid.NewGuid()), default)); // wrong merchant
        Assert.Null(await handler.Handle(new GetCartQuery(Guid.NewGuid(), Merchant), default));  // missing
    }

    [Fact]
    public async Task RemoveItem_drops_the_line_and_saves()
    {
        var cart = SeededCart(out var cartId);
        var uow = new FakeUnitOfWork();
        var handler = new RemoveItemFromCartHandler(new FakeCartRepository(cart), uow);

        var view = await handler.Handle(new RemoveItemFromCartCommand(cartId, Merchant, Product), default);

        Assert.Empty(view.Items);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task SetQuantity_updates_the_line()
    {
        var cart = SeededCart(out var cartId);
        var handler = new SetCartItemQuantityHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        var view = await handler.Handle(new SetCartItemQuantityCommand(cartId, Merchant, Product, 9), default);

        Assert.Equal(9, view.Items.Single().Quantity);
    }

    [Fact]
    public async Task SetQuantity_rejects_a_non_positive_value()
    {
        var cart = SeededCart(out var cartId);
        var handler = new SetCartItemQuantityHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await handler.Handle(new SetCartItemQuantityCommand(cartId, Merchant, Product, 0), default));
    }

    [Fact]
    public async Task ClearCart_empties_it()
    {
        var cart = SeededCart(out var cartId);
        var handler = new ClearCartHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        var view = await handler.Handle(new ClearCartCommand(cartId, Merchant), default);

        Assert.Empty(view.Items);
    }

    // REQ-2.1/2.5 — the two halves of the cart-checkout cycle the /checkouts endpoints orchestrate: freeze
    // after a checkout starts, unfreeze after it is abandoned. Each is its own unit of work.
    [Fact]
    public async Task MarkCheckedOut_freezes_the_cart_and_saves()
    {
        var cart = SeededCart(out var cartId);
        var uow = new FakeUnitOfWork();

        await new MarkCartCheckedOutHandler(new FakeCartRepository(cart), uow)
            .Handle(new MarkCartCheckedOutCommand(cartId, Merchant), default);

        Assert.Equal(1, uow.SaveCount);
        Assert.Throws<InvalidOperationException>(() => cart.Clear());
    }

    [Fact]
    public async Task Reopen_unfreezes_the_cart_and_saves()
    {
        var cart = SeededCart(out var cartId);
        cart.MarkCheckedOut();
        var uow = new FakeUnitOfWork();

        await new ReopenCartHandler(new FakeCartRepository(cart), uow)
            .Handle(new ReopenCartCommand(cartId, Merchant), default);

        Assert.Equal(1, uow.SaveCount);
        cart.Clear(); // no longer frozen
        Assert.Empty(cart.Items);
    }

    [Fact]
    public async Task Freezing_a_cart_owned_by_another_merchant_is_rejected()
    {
        var cart = SeededCart(out var cartId);
        var handler = new MarkCartCheckedOutHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new MarkCartCheckedOutCommand(cartId, Guid.NewGuid()), default));
    }

    [Fact]
    public async Task An_edit_on_a_missing_cart_is_rejected()
    {
        var handler = new ClearCartHandler(new FakeCartRepository(), new FakeUnitOfWork());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new ClearCartCommand(Guid.NewGuid(), Merchant), default));
    }
}
