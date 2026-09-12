using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Carts.Application;

/// <summary>
/// Loads the open cart, adds the document line, and commits. Rejects an unknown cart, one owned by another
/// merchant, or a document the cart already holds (<c>Cart.AddItem</c> -> 400, REQ-9.4).
/// </summary>
public sealed class AddItemToCartHandler : ICommandHandler<AddItemToCartCommand, CartView>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;

    public AddItemToCartHandler(ICartRepository carts, IUnitOfWork unitOfWork)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<CartView> Handle(AddItemToCartCommand command, CancellationToken cancellationToken)
    {
        var cart = await _carts.GetAsync(command.CartId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Cart {command.CartId} was not found.");

        if (cart.MerchantId != command.MerchantId)
            throw new InvalidOperationException($"Cart {command.CartId} does not belong to the requesting merchant.");
        if (command.ExpectedVersion is { } expected && cart.Version != expected)
            throw new ConcurrencyConflictException("Cart changed after it was read.");

        cart.AddItem(
            command.ProductCode, command.SaleCode, command.VariantCode, command.VariantName,
            command.Quantity, command.UnitPrice, command.Metadata);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return CartView.From(cart);
    }
}
