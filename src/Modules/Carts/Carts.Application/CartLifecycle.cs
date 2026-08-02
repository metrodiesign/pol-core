using BuildingBlocks.Application;
using Mediator;

namespace Carts.Application;

/// <summary>Freezes the cart behind a checkout session (REQ-2.1). The checkout endpoint sends it in a
/// SECOND unit of work, right after StartCheckoutCommand commits — the two aggregates live in different
/// modules, so the freeze is orchestrated at the host, not inside the checkout handler.</summary>
public sealed record MarkCartCheckedOutCommand(Guid CartId, Guid MerchantId) : ICommand<Unit>, IMerchantScoped;

/// <summary>Unfreezes the cart behind an abandoned checkout session (REQ-2.5). Idempotent — reopening an
/// already-open cart changes nothing (REQ-2.9).</summary>
public sealed record ReopenCartCommand(Guid CartId, Guid MerchantId) : ICommand<Unit>, IMerchantScoped;

public sealed class MarkCartCheckedOutHandler : ICommandHandler<MarkCartCheckedOutCommand, Unit>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;

    public MarkCartCheckedOutHandler(ICartRepository carts, IUnitOfWork unitOfWork)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Unit> Handle(MarkCartCheckedOutCommand command, CancellationToken cancellationToken)
    {
        var cart = await CartLoad.RequireAsync(_carts, command.CartId, command.MerchantId, cancellationToken).ConfigureAwait(false);
        cart.MarkCheckedOut();
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed class ReopenCartHandler : ICommandHandler<ReopenCartCommand, Unit>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;

    public ReopenCartHandler(ICartRepository carts, IUnitOfWork unitOfWork)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<Unit> Handle(ReopenCartCommand command, CancellationToken cancellationToken)
    {
        var cart = await CartLoad.RequireAsync(_carts, command.CartId, command.MerchantId, cancellationToken).ConfigureAwait(false);
        cart.Reopen();
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
