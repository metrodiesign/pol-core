using Payments.Domain;

namespace Payments.Application.Ports;

/// <summary>
/// Persistence port for <see cref="PaymentSession"/> aggregates. Implementations live in
/// Payments.Infrastructure over the shared txn data plane; the unit of work commits separately.
/// </summary>
public interface IPaymentSessionRepository
{
    /// <summary>Tracks a new session for insertion. Commit happens via the unit of work.</summary>
    void Add(PaymentSession session);

    Task<PaymentSession?> GetByIdAsync(Guid paymentSessionId, CancellationToken cancellationToken);

    /// <summary>Looks a session up by the (PSP, external charge id) pair the webhook path resolves on.</summary>
    Task<PaymentSession?> GetByExternalChargeAsync(PspCode psp, string externalChargeId, CancellationToken cancellationToken);
}
