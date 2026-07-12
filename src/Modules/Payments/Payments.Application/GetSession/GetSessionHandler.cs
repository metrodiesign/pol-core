using Mediator;
using Payments.Application.Ports;

namespace Payments.Application.GetSession;

/// <summary>Loads a payment session and projects it to a <see cref="SessionView"/>.</summary>
public sealed class GetSessionHandler : IQueryHandler<GetSessionQuery, SessionView>
{
    private readonly ISessionRepository _sessions;

    public GetSessionHandler(ISessionRepository sessions) => _sessions = sessions;

    public async ValueTask<SessionView> Handle(
        GetSessionQuery query,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetByIdAsync(query.PaymentSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PaymentSession {query.PaymentSessionId} not found.");

        return new SessionView(
            session.Id,
            session.OrderId,
            session.MerchantId,
            session.Amount,
            session.Method,
            session.Psp,
            session.Status,
            session.PspExternalChargeId,
            session.RedirectUrl,
            session.CreatedAt,
            session.UpdatedAt);
    }
}
