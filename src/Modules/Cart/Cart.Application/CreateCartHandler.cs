using BuildingBlocks.Application;
using Mediator;
using CartAggregate = Cart.Domain.Cart;

namespace Cart.Application;

/// <summary>Creates a fresh open cart, tracks it, and commits via the shared unit of work.</summary>
public sealed class CreateCartHandler : ICommandHandler<CreateCartCommand, Guid>
{
    private readonly ICartRepository _carts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateCartHandler(ICartRepository carts, IUnitOfWork unitOfWork, IClock clock)
    {
        _carts = carts;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<Guid> Handle(CreateCartCommand command, CancellationToken cancellationToken)
    {
        var cart = new CartAggregate(Guid.CreateVersion7(), command.TenantId, _clock.UtcNow);
        _carts.Add(cart);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return cart.Id;
    }
}
