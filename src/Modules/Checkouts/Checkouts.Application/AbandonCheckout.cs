using BuildingBlocks.Application;
using Checkouts.Domain;
using Mediator;

namespace Checkouts.Application;

/// <summary>Cancels a live checkout so the cart behind it can be edited and checked out again (REQ-2.5).</summary>
public sealed record AbandonCheckoutCommand(Guid CheckoutSessionId, Guid MerchantId)
    : ICommand<AbandonCheckoutResult>, IMerchantScoped;

/// <summary>The abandoned session's cart (the caller reopens it) and its resulting status.</summary>
public sealed record AbandonCheckoutResult(Guid CartId, SessionStatus Status);

/// <summary>
/// Transitions the session to <see cref="SessionStatus.Abandoned"/>. Rejecting a confirmed session (409) and
/// no-op'ing an already-abandoned one (REQ-2.6/2.9) are the aggregate's rules, not this handler's. Reopening
/// the cart is a separate unit of work in the Carts module, orchestrated by the endpoint.
/// </summary>
public sealed class AbandonCheckoutHandler : ICommandHandler<AbandonCheckoutCommand, AbandonCheckoutResult>
{
    private readonly ICheckoutRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public AbandonCheckoutHandler(ICheckoutRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<AbandonCheckoutResult> Handle(AbandonCheckoutCommand command, CancellationToken cancellationToken)
    {
        var session = await _repository.GetByIdAsync(command.CheckoutSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"Checkout session {command.CheckoutSessionId} was not found.");

        session.Abandon();
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AbandonCheckoutResult(session.CartId, session.Status);
    }
}
