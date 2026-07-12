using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Ports;
using Payments.Domain;

namespace Payments.Application.CreatePaymentSession;

/// <summary>Persists a new <see cref="PaymentSession"/> in the <see cref="PaymentStatus.Created"/> state.</summary>
public sealed class CreatePaymentSessionHandler
    : ICommandHandler<CreatePaymentSessionCommand, CreatePaymentSessionResult>
{
    private readonly IPaymentSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreatePaymentSessionHandler(
        IPaymentSessionRepository sessions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _sessions = sessions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async ValueTask<CreatePaymentSessionResult> Handle(
        CreatePaymentSessionCommand command,
        CancellationToken cancellationToken)
    {
        var session = PaymentSession.Create(
            command.MerchantId,
            command.OrderId,
            command.Amount,
            command.Method,
            command.Psp,
            _clock.UtcNow);

        _sessions.Add(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new CreatePaymentSessionResult(session.Id);
    }
}
