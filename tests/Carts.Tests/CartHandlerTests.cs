using Carts.Application;
using SharedKernel;
using CartAggregate = Carts.Domain.Cart;

namespace Carts.Tests;

/// <summary>Merchant real API REQ-5.10/6.1/6.5/6.6/6.7/6.8/6.21: authoritative cart state.</summary>
public sealed class CartHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.NewGuid();
    private const string DocA = "00098-69100/กธ/900001-10";
    private const string DocB = "00098-69100/กธ/900002-10";
    private const string SaleCode = "77001";
    private const string Group = "VMI";
    private static readonly CommerceItemMetadata Metadata = new(
        CommerceItemMetadataCodec.InsuranceDocumentSource, "POLICY", "P-001", null, null);

    private static CartAggregate SeededCart(out Guid cartId, out Guid itemId)
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, SaleCode, Now);
        cart.AddItem(DocA, SaleCode, Group, "Motor", 2, Money.Of(150m, "THB"), Metadata);
        cartId = cart.Id;
        itemId = cart.Items.First().Id;
        return cart;
    }

    [Fact]
    public async Task CreateCart_snapshots_server_sale_code()
    {
        var carts = new FakeCartRepository();
        var uow = new FakeUnitOfWork();
        var handler = new CreateCartHandler(carts, uow, new FakeClock(Now));

        var id = await handler.Handle(new CreateCartCommand(Merchant, " 77001 "), default);

        var cart = Assert.Single(carts.Carts);
        Assert.Equal(id, cart.Id);
        Assert.Equal("77001", cart.SaleCode);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task AddItem_preserves_typed_server_snapshot_and_quantity_total()
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, SaleCode, Now);
        var uow = new FakeUnitOfWork();
        var handler = new AddItemToCartHandler(new FakeCartRepository(cart), uow);

        var result = await handler.Handle(new AddItemToCartCommand(
            cart.Id, Merchant, DocA, SaleCode, Group, "Motor policy", 4,
            Money.Of(150m, "THB"), Metadata), default);

        var line = Assert.Single(cart.Items);
        Assert.Equal(DocA, line.ProductCode);
        Assert.Equal(Group, line.VariantCode);
        Assert.Equal("Motor policy", line.VariantName);
        Assert.Equal(Money.Of(600m, "THB"), line.LineTotal);
        Assert.Equal(CommerceItemMetadataCodec.Serialize(Metadata), line.Metadata);
        Assert.Equal(Money.Of(600m, "THB"), result.Subtotal);
        Assert.Single(result.Items);
        Assert.Equal(line.Id, result.Items[0].ItemId);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task AddItem_rejects_cart_owned_by_another_merchant_without_saving()
    {
        var cart = new CartAggregate(Guid.NewGuid(), Merchant, SaleCode, Now);
        var uow = new FakeUnitOfWork();
        var handler = new AddItemToCartHandler(new FakeCartRepository(cart), uow);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await handler.Handle(
            new AddItemToCartCommand(cart.Id, Guid.NewGuid(), DocA, SaleCode, Group, "Motor", 1,
                Money.Of(150m, "THB"), Metadata), default));

        Assert.Empty(cart.Items);
        Assert.Equal(0, uow.SaveCount);
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

    [Fact]
    public async Task An_edit_on_a_missing_cart_is_rejected()
    {
        var handler = new ClearCartHandler(new FakeCartRepository(), new FakeUnitOfWork());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new ClearCartCommand(Guid.NewGuid(), Merchant), default));
    }
}
