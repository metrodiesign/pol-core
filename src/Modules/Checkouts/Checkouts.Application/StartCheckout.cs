using BuildingBlocks.Application;
using Checkouts.Domain;
using Mediator;
using SharedKernel;

namespace Checkouts.Application;

/// <summary>Opens a new checkout session for a cart with an agreed amount + an optional notification
/// recipient (the customer's email/phone, carried to the order on confirm).</summary>
public sealed record StartCheckoutCommand(
    Guid MerchantId, Guid CartId, Money Amount, string? Recipient = null)
    : ICommand<StartCheckoutResult>, IMerchantScoped;

/// <summary>Identity of the freshly started checkout session.</summary>
public sealed record StartCheckoutResult(Guid CheckoutSessionId);

/// <summary>Opens a <see cref="CheckoutSession"/> and persists it.</summary>
public sealed class StartCheckoutHandler : ICommandHandler<StartCheckoutCommand, StartCheckoutResult>
{
    private readonly ICheckoutRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public StartCheckoutHandler(ICheckoutRepository repository, IUnitOfWork unitOfWork, IClock clock)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<StartCheckoutResult> Handle(StartCheckoutCommand command, CancellationToken cancellationToken)
    {
        var session = CheckoutSession.Start(command.MerchantId, command.CartId, command.Amount, _clock.UtcNow, command.Recipient);

        _repository.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StartCheckoutResult(session.Id);
    }
}
