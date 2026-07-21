using BuildingBlocks.Application;
using Checkouts.Domain;
using Contracts;
using Mediator;

namespace Checkouts.Application;

/// <summary>Confirms a started checkout session.</summary>
public sealed record ConfirmCheckoutCommand(Guid CheckoutSessionId, Guid MerchantId)
    : ICommand<ConfirmCheckoutResult>, IMerchantScoped;

/// <summary>Outcome of the confirmation: the session id and its resulting status.</summary>
public sealed record ConfirmCheckoutResult(Guid CheckoutSessionId, SessionStatus Status);

/// <summary>
/// Transitions the session to <see cref="SessionStatus.Confirmed"/> and emits <see cref="CheckoutConfirmed"/>
/// in the SAME unit of work (transactional outbox), so the Orders module opens the order out-of-band. Keeps
/// the modules decoupled — Checkout raises an event, it does not call Orders directly.
/// </summary>
public sealed class ConfirmCheckoutHandler : ICommandHandler<ConfirmCheckoutCommand, ConfirmCheckoutResult>
{
    private readonly ICheckoutRepository _repository;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ConfirmCheckoutHandler(ICheckoutRepository repository, IOutbox outbox, IUnitOfWork unitOfWork, IClock clock)
    {
        _repository = repository;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<ConfirmCheckoutResult> Handle(ConfirmCheckoutCommand command, CancellationToken cancellationToken)
    {
        var session = await _repository.GetByIdAsync(command.CheckoutSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Checkout session {command.CheckoutSessionId} was not found.");

        session.Confirm();

        var lines = session.Lines
            .Select(l => new CheckoutConfirmedLine(
                l.ProductId, l.Quantity, l.UnitPrice, l.SumInsured, l.CoverageDurationDays, l.Insurer,
                l.InsuredFirstName, l.InsuredLastName, l.InsuredIdNumber, l.InsuredDateOfBirth))
            .ToList();
        _outbox.Enqueue(new CheckoutConfirmed(
            session.MerchantId, session.Id, session.Amount,
            session.NotificationRecipient, _clock.UtcNow, lines));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ConfirmCheckoutResult(session.Id, session.Status);
    }
}
