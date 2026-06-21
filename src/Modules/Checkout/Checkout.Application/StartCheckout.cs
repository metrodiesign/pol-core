using BuildingBlocks.Application;
using Checkout.Domain;
using Mediator;
using SharedKernel;

namespace Checkout.Application;

/// <summary>Opens a new checkout session for a cart with an agreed amount.</summary>
public sealed record StartCheckoutCommand(Guid TenantId, Guid CartId, long AmountMinorUnits, string Currency)
    : ICommand<StartCheckoutResult>, ITenantScoped;

/// <summary>Identity of the freshly started checkout session.</summary>
public sealed record StartCheckoutResult(Guid CheckoutSessionId);

/// <summary>Validates the amount, opens a <see cref="CheckoutSession"/> and persists it.</summary>
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
        var amount = Money.Of(command.AmountMinorUnits, command.Currency);
        var session = CheckoutSession.Start(command.TenantId, command.CartId, amount, _clock.UtcNow);

        _repository.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new StartCheckoutResult(session.Id);
    }
}
