using BuildingBlocks.Application;
using Mediator;

namespace Carts.Application;

/// <summary>Removes a product line from an open cart and returns the updated view.</summary>
public sealed record RemoveItemFromCartCommand(Guid CartId, Guid MerchantId, Guid ProductId)
    : ICommand<CartView>, IMerchantScoped;

/// <summary>Sets a line's quantity on an open cart (quantity must be positive) and returns the updated view.</summary>
public sealed record SetCartItemQuantityCommand(Guid CartId, Guid MerchantId, Guid ProductId, int Quantity)
    : ICommand<CartView>, IMerchantScoped;

/// <summary>Empties an open cart and returns the updated view.</summary>
public sealed record ClearCartCommand(Guid CartId, Guid MerchantId) : ICommand<CartView>, IMerchantScoped;

/// <summary>Loads the cart for the bound merchant or rejects it (mirrors AddItemToCartHandler): RLS already
/// filters cross-merchant rows, and the explicit owner check is the belt-and-braces.</summary>
internal static class CartLoad
{
    public static async Task<Domain.Cart> RequireAsync(
        ICartRepository carts, Guid cartId, Guid merchantId, CancellationToken cancellationToken)
    {
        var cart = await carts.GetAsync(cartId, cancellationToken).ConfigureAwait(false);
        if (cart is null || cart.MerchantId != merchantId)
            throw new InvalidOperationException($"Cart {cartId} was not found.");
        return cart;
    }
}

public sealed class RemoveItemFromCartHandler : ICommandHandler<RemoveItemFromCartCommand, CartView>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;

    public RemoveItemFromCartHandler(ICartRepository carts, IUnitOfWork unitOfWork)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<CartView> Handle(RemoveItemFromCartCommand command, CancellationToken cancellationToken)
    {
        var cart = await CartLoad.RequireAsync(_carts, command.CartId, command.MerchantId, cancellationToken).ConfigureAwait(false);
        cart.RemoveItem(command.ProductId);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CartView.From(cart);
    }
}

public sealed class SetCartItemQuantityHandler : ICommandHandler<SetCartItemQuantityCommand, CartView>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;

    public SetCartItemQuantityHandler(ICartRepository carts, IUnitOfWork unitOfWork)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<CartView> Handle(SetCartItemQuantityCommand command, CancellationToken cancellationToken)
    {
        var cart = await CartLoad.RequireAsync(_carts, command.CartId, command.MerchantId, cancellationToken).ConfigureAwait(false);
        cart.SetItemQuantity(command.ProductId, command.Quantity); // qty<=0 -> ArgumentOutOfRangeException -> 400
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CartView.From(cart);
    }
}

public sealed class ClearCartHandler : ICommandHandler<ClearCartCommand, CartView>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;

    public ClearCartHandler(ICartRepository carts, IUnitOfWork unitOfWork)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<CartView> Handle(ClearCartCommand command, CancellationToken cancellationToken)
    {
        var cart = await CartLoad.RequireAsync(_carts, command.CartId, command.MerchantId, cancellationToken).ConfigureAwait(false);
        cart.Clear();
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return CartView.From(cart);
    }
}
