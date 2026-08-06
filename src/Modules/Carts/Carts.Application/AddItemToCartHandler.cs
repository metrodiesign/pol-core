using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Carts.Application;

/// <summary>
/// Loads the open cart, adds the document line, and commits. Rejects an unknown cart, one owned by another
/// merchant, or a document the cart already holds (<c>Cart.AddItem</c> -> 400, REQ-9.4).
/// </summary>
public sealed class AddItemToCartHandler : ICommandHandler<AddItemToCartCommand, AddItemResult>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;

    public AddItemToCartHandler(ICartRepository carts, IUnitOfWork unitOfWork)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<AddItemResult> Handle(AddItemToCartCommand command, CancellationToken cancellationToken)
    {
        var cart = await _carts.GetAsync(command.CartId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Cart {command.CartId} was not found.");

        if (cart.MerchantId != command.MerchantId)
            throw new InvalidOperationException($"Cart {command.CartId} does not belong to the requesting merchant.");

        cart.AddItem(
            command.DocumentNo, command.SaleCode, command.ProductGroup, command.Quantity, command.UnitPrice);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var subtotal = cart.Subtotal ?? Money.Zero(command.UnitPrice.Currency);
        return new AddItemResult(cart.Id, cart.Items.Count, subtotal);
    }
}
