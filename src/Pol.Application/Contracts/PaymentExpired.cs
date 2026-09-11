using Mediator;

namespace Contracts;

/// <summary>PII-free payment-attempt expiry emitted exactly once with the Session transition.</summary>
public sealed record PaymentExpired(
    Guid EventId,
    Guid PaymentSessionId,
    Guid OrderId,
    Guid MerchantId,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "payments.payment-expired.v1";
    public const string SchemaVersion = "v1";
}
