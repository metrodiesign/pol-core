using Mediator;
using SharedKernel;

namespace Contracts;

/// <summary>
/// PaymentPaid v2 — the cross-module integration event the Payments module emits when a
/// PSP-confirmed payment transitions to Paid (PLAN decisions #2, #10, #15). It is published via
/// the transactional outbox and consumed idempotently by Orders, which re-verifies
/// <see cref="Amount"/> (amount + currency) against its own record before fulfilling.
/// </summary>
public sealed record PaymentPaid(
    Guid EventId,
    Guid PaymentSessionId,
    Guid OrderId,
    Guid MerchantId,
    Money Amount,
    string Method,
    string PspCode,
    string ExternalChargeId,
    string PspEventId,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "payments.payment-paid.v2";
    public const string SchemaVersion = "v2";
}
