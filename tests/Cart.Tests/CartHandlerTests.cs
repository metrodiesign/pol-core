using Cart.Application;
using SharedKernel;
using CartAggregate = Cart.Domain.Cart;

namespace Cart.Tests;

public sealed class CartHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();

    private static CartAggregate SeededCart(out Guid cartId)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Tenant, Now);
        cart.AddItem(Product, 2, Money.Of(150, "THB"));
        cartId = cart.Id;
        return cart;
    }

    [Fact]
    public async Task GetCart_returns_the_view_for_its_own_tenant()
    {
        var cart = SeededCart(out var cartId);
        var handler = new GetCartHandler(new FakeCartRepository(cart));

        var view = await handler.Handle(new GetCartQuery(cartId, Tenant), default);

        Assert.NotNull(view);
        Assert.Single(view!.Items);
        Assert.Equal(300, view.SubtotalMinorUnits);
        Assert.Equal("THB", view.SubtotalCurrency);
    }

    [Fact]
    public async Task GetCart_returns_null_for_another_tenant_or_missing()
    {
        var cart = SeededCart(out var cartId);
        var handler = new GetCartHandler(new FakeCartRepository(cart));

        Assert.Null(await handler.Handle(new GetCartQuery(cartId, Guid.NewGuid()), default)); // wrong tenant
        Assert.Null(await handler.Handle(new GetCartQuery(Guid.NewGuid(), Tenant), default));  // missing
    }

    [Fact]
    public async Task RemoveItem_drops_the_line_and_saves()
    {
        var cart = SeededCart(out var cartId);
        var uow = new FakeUnitOfWork();
        var handler = new RemoveItemFromCartHandler(new FakeCartRepository(cart), uow);

        var view = await handler.Handle(new RemoveItemFromCartCommand(cartId, Tenant, Product), default);

        Assert.Empty(view.Items);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task SetQuantity_updates_the_line()
    {
        var cart = SeededCart(out var cartId);
        var handler = new SetCartItemQuantityHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        var view = await handler.Handle(new SetCartItemQuantityCommand(cartId, Tenant, Product, 9), default);

        Assert.Equal(9, view.Items.Single().Quantity);
    }

    [Fact]
    public async Task SetQuantity_rejects_a_non_positive_value()
    {
        var cart = SeededCart(out var cartId);
        var handler = new SetCartItemQuantityHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await handler.Handle(new SetCartItemQuantityCommand(cartId, Tenant, Product, 0), default));
    }

    [Fact]
    public async Task ClearCart_empties_it()
    {
        var cart = SeededCart(out var cartId);
        var handler = new ClearCartHandler(new FakeCartRepository(cart), new FakeUnitOfWork());

        var view = await handler.Handle(new ClearCartCommand(cartId, Tenant), default);

        Assert.Empty(view.Items);
    }

    [Fact]
    public async Task An_edit_on_a_missing_cart_is_rejected()
    {
        var handler = new ClearCartHandler(new FakeCartRepository(), new FakeUnitOfWork());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new ClearCartCommand(Guid.NewGuid(), Tenant), default));
    }
}
