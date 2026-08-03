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

        var items = session.Items
            .Select(i => new CheckoutConfirmedItem(
                i.ProductId, i.Quantity, i.UnitPrice,
                i.DocumentNo, i.ProductGroup, i.DocumentType, i.PolicyNumber, i.StartDate, i.EndDate,
                i.InsuredFirstName, i.InsuredLastName, i.InsuredIdNumber, i.InsuredDateOfBirth,
                i.Discount.Amount, i.Discount.Currency))
            .ToList();
        // Recipient stays filled with the same value the consumer would derive, so the payload is readable
        // by a pre-REQ-6.6 consumer too — the new fields are additive, not a cutover (REQ-6.8/7.5).
        _outbox.Enqueue(new CheckoutConfirmed(
            session.MerchantId, session.Id, session.Amount,
            session.Customer.NotificationRecipient, _clock.UtcNow, items,
            session.Channel.ToString(), session.CustomerName, session.CustomerPhone, session.CustomerEmail));

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new ConfirmCheckoutResult(session.Id, session.Status);
    }
}
