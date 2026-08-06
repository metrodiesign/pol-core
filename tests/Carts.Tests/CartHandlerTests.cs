using Carts.Application;
using SharedKernel;
using CartAggregate = Carts.Domain.Cart;

namespace Carts.Tests;

public sealed class CartHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.NewGuid();
    private const string DocA = "00098-69100/กธ/900001-10";
    private const string DocB = "00098-69100/กธ/900002-10";
    private const string SaleCode = "77001";
    private const string Group = "VMI";

    private static CartAggregate SeededCart(out Guid cartId, out Guid itemId)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, Now);
        cart.AddItem(DocA, SaleCode, Group, 2, Money.Of(150m, "THB"));
        cartId = cart.Id;
        itemId = cart.Items.First().Id;
        return cart;
    }

    [Fact]
    public async Task GetCart_returns_the_view_for_its_own_tenant()
    {
        var cart = SeededCart(out var cartId, out _);
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
        var cart = SeededCart(out var cartId, out _);
        var handler = new GetCartHandler(new FakeCartRepository(cart));

        Assert.Null(await handler.Handle(new GetCartQuery(cartId, Guid.NewGuid()), default)); // wrong merchant
        Assert.Null(await handler.Handle(new GetCartQuery(Guid.NewGuid(), Merchant), default));  // missing
    }

    [Fact]
    public async Task RemoveItem_drops_the_line_and_saves()
    {
        var cart = SeededCart(out var cartId, out var itemId);
        var uow = new FakeUnitOfWork();
        var handler = new RemoveItemFromCartHandler(new FakeCartRepository(cart), uow);

        var view = await handler.Handle(new RemoveItemFromCartCommand(cartId, Merchant, itemId), default);

        Assert.Empty(view.Items);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task SetQuantity_updates_the_line()
    {
        var cart = SeededCart(out var cartId, out var itemId);
        var handler = new SetCartItemQuantityHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        var view = await handler.Handle(new SetCartItemQuantityCommand(cartId, Merchant, itemId, 9), default);

        Assert.Equal(9, view.Items.Single().Quantity);
    }

    [Fact]
    public async Task SetQuantity_rejects_a_non_positive_value()
    {
        var cart = SeededCart(out var cartId, out var itemId);
        var handler = new SetCartItemQuantityHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await handler.Handle(new SetCartItemQuantityCommand(cartId, Merchant, itemId, 0), default));
    }

    // REQ-9.3 — a remove/quantity route addressing an itemId not in the cart is a 404 (NotFoundException).
    [Fact]
    public async Task RemoveItem_with_an_unknown_itemId_is_a_not_found()
    {
        var cart = SeededCart(out var cartId, out _);
        var handler = new RemoveItemFromCartHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        await Assert.ThrowsAsync<BuildingBlocks.Application.NotFoundException>(async () =>
            await handler.Handle(new RemoveItemFromCartCommand(cartId, Merchant, Guid.NewGuid()), default));
    }

    [Fact]
    public async Task ClearCart_empties_it()
    {
        var cart = SeededCart(out var cartId, out _);
        var handler = new ClearCartHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        var view = await handler.Handle(new ClearCartCommand(cartId, Merchant), default);

        Assert.Empty(view.Items);
    }

    // REQ-2.1/2.5 — the two halves of the cart-checkout cycle the /checkouts endpoints orchestrate: freeze
    // after a checkout starts, unfreeze after it is abandoned. Each is its own unit of work.
    [Fact]
    public async Task MarkCheckedOut_freezes_the_cart_and_saves()
    {
        var cart = SeededCart(out var cartId, out _);
        var uow = new FakeUnitOfWork();

        await new MarkCartCheckedOutHandler(new FakeCartRepository(cart), uow)
            .Handle(new MarkCartCheckedOutCommand(cartId, Merchant, cart.Version), default);

        Assert.Equal(1, uow.SaveCount);
        Assert.Throws<InvalidOperationException>(() => cart.Clear());
    }

    // PR #166 review — the freeze race: an edit that landed after the checkout snapshot was read makes the
    // cart's Version differ from the command's ExpectedVersion, and the freeze must refuse (409) rather
    // than freeze a cart whose lines no longer match the session.
    [Fact]
    public async Task MarkCheckedOut_with_a_stale_version_is_rejected_and_never_saves()
    {
        var cart = SeededCart(out var cartId, out _);
        var staleVersion = cart.Version;
        cart.AddItem(DocB, SaleCode, Group, 1, Money.Of(150m, "THB")); // concurrent edit after the snapshot
        var uow = new FakeUnitOfWork();
        var handler = new MarkCartCheckedOutHandler(new FakeCartRepository(cart), uow);

        await Assert.ThrowsAsync<BuildingBlocks.Application.ConcurrencyConflictException>(async () =>
            await handler.Handle(new MarkCartCheckedOutCommand(cartId, Merchant, staleVersion), default));

        Assert.Equal(0, uow.SaveCount);
        Assert.Equal(nameof(Carts.Domain.CartStatus.Open), cart.Status.ToString());
    }

    [Fact]
    public async Task Reopen_unfreezes_the_cart_and_saves()
    {
        var cart = SeededCart(out var cartId, out _);
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
        var cart = SeededCart(out var cartId, out _);
        var handler = new MarkCartCheckedOutHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new MarkCartCheckedOutCommand(cartId, Guid.NewGuid(), cart.Version), default));
    }

    [Fact]
    public async Task An_edit_on_a_missing_cart_is_rejected()
    {
        var handler = new ClearCartHandler(new FakeCartRepository(), new FakeUnitOfWork());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new ClearCartCommand(Guid.NewGuid(), Merchant), default));
    }
}
