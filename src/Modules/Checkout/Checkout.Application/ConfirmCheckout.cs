using BuildingBlocks.Application;
using Checkout.Domain;
using Mediator;

namespace Checkout.Application;

/// <summary>Confirms a started checkout session.</summary>
public sealed record ConfirmCheckoutCommand(Guid CheckoutSessionId, Guid TenantId)
    : ICommand<ConfirmCheckoutResult>, ITenantScoped;

/// <summary>Outcome of the confirmation: the session id and its resulting status.</summary>
public sealed record ConfirmCheckoutResult(Guid CheckoutSessionId, CheckoutStatus Status);

/// <summary>
/// Transitions the session to <see cref="CheckoutStatus.Confirmed"/>.
/// </summary>
// ponytail: Confirm just transitions state; wiring to Orders.CreateOrder / Payments (raising the
// cross-module command/notification) is a later task. Kept self-contained on purpose.
public sealed class ConfirmCheckoutHandler : ICommandHandler<ConfirmCheckoutCommand, ConfirmCheckoutResult>
{
    private readonly ICheckoutRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ConfirmCheckoutHandler(ICheckoutRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask<ConfirmCheckoutResult> Handle(ConfirmCheckoutCommand command, CancellationToken cancellationToken)
    {
        var session = await _repository.GetByIdAsync(command.CheckoutSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Checkout session {command.CheckoutSessionId} was not found.");

        session.Confirm();
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ConfirmCheckoutResult(session.Id, session.Status);
    }
}
