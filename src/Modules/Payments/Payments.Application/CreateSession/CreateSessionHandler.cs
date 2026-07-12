using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Application.CreateSession;

/// <summary>Persists a new <see cref="Session"/> in the <see cref="SessionStatus.Created"/> state.</summary>
public sealed class CreateSessionHandler
    : ICommandHandler<CreateSessionCommand, CreateSessionResult>
{
    private readonly ISessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateSessionHandler(
        ISessionRepository sessions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _sessions = sessions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<CreateSessionResult> Handle(
        CreateSessionCommand command,
        CancellationToken cancellationToken)
    {
        var session = Session.Create(
            command.MerchantId,
            command.OrderId,
            command.Amount,
            command.Method,
            command.Psp,
            _clock.UtcNow);

        _sessions.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreateSessionResult(session.Id);
    }
}
