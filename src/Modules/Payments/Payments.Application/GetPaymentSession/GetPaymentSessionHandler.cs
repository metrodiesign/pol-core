using Mediator;
using Payments.Application.Ports;

namespace Payments.Application.GetPaymentSession;

/// <summary>Loads a payment session and projects it to a <see cref="PaymentSessionView"/>.</summary>
public sealed class GetPaymentSessionHandler : IQueryHandler<GetPaymentSessionQuery, PaymentSessionView>
{
    private readonly IPaymentSessionRepository _sessions;

    public GetPaymentSessionHandler(IPaymentSessionRepository sessions) => _sessions = sessions;

    public async ValueTask<PaymentSessionView> Handle(
        GetPaymentSessionQuery query,
        CancellationToken cancellationToken)
    {
        var session = await _sessions.GetByIdAsync(query.PaymentSessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"PaymentSession {query.PaymentSessionId} not found.");

        return new PaymentSessionView(
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
