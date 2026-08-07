using Mediator;

namespace Contracts;

/// <summary>PII-free payment-attempt failure emitted exactly once with the Session transition.</summary>
public sealed record PaymentFailed(
    Guid EventId,
    Guid PaymentSessionId,
    Guid OrderId,
    Guid MerchantId,
    string ReasonCode,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "payments.payment-failed.v1";
    public const string SchemaVersion = "v1";
}
