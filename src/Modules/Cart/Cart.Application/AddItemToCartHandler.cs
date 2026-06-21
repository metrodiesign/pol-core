using BuildingBlocks.Application;
using Mediator;
using SharedKernel;

namespace Cart.Application;

/// <summary>
/// Loads the open cart, adds (or merges) the product line after re-validating the unit price into a
/// <see cref="Money"/>, and commits. Rejects an unknown cart or one owned by another tenant.
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

        if (cart.TenantId != command.TenantId)
            throw new InvalidOperationException($"Cart {command.CartId} does not belong to the requesting tenant.");

        var unitPrice = Money.Of(command.UnitPriceMinorUnits, command.Currency);
        cart.AddItem(command.ProductId, command.Quantity, unitPrice);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var subtotal = cart.Subtotal ?? Money.Zero(command.Currency);
        return new AddItemResult(cart.Id, cart.Items.Count, subtotal.MinorUnits, subtotal.Currency);
    }
}
