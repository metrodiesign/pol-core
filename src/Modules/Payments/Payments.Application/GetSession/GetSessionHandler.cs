using BuildingBlocks.Application;
using Mediator;
using Payments.Application.Ports;

namespace Payments.Application.GetSession;

/// <summary>Loads a payment session and projects it to a <see cref="SessionView"/>. A session that is not
/// there — or belongs to another company, which the merchant query filter makes indistinguishable — is a
/// <see cref="NotFoundException"/> (404), not the 409 an <c>InvalidOperationException</c> would have
/// produced: absence is not a state conflict, and a read that answers 409 tells a caller its request was
/// well-formed but refused (REQ-8.8).</summary>
public sealed class GetSessionHandler : IQueryHandler<GetSessionQuery, SessionView>
{
    private readonly ISessionRepository _sessions;

    public GetSessionHandler(ISessionRepository sessions) => _sessions = sessions;

    public async ValueTask<SessionView> Handle(
        GetSessionQuery query,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetByIdAsync(query.PaymentSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException($"PaymentSession {query.PaymentSessionId} not found.");

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
            session.UpdatedAt,
            session.Version);
    }
}
