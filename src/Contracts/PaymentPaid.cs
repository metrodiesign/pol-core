using Mediator;
using SharedKernel;

namespace Contracts;

/// <summary>
/// PaymentPaid v1 — the single cross-module integration event the Payments module emits when a
/// PSP-confirmed payment transitions to Paid (PLAN decisions #2, #10, #15). It is published via
/// the transactional outbox and consumed idempotently by Orders, which re-verifies
/// <see cref="Amount"/> (amount + currency) against its own record before fulfilling.
/// </summary>
public sealed record PaymentPaid(
    Guid PaymentSessionId,
    Guid OrderId,
    Guid TenantId,
    Money Amount,
    string PspCode,
    string ExternalChargeId,
    string EventId,
    DateTime OccurredAtUtc) : INotification
{
    public const string SchemaVersion = "v1";
}
