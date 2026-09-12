using Mediator;

namespace Contracts;

/// <summary>Protected payment-link notification request. The event id is the PaymentLink id, so replaying
/// the same issue/rotate operation cannot materialize a second notification.</summary>
public sealed record PaymentLinkNotificationRequestedV1(
    Guid EventId,
    Guid MerchantId,
    Guid OrderId,
    Guid LinkId,
    string? Email,
    string? PhoneNumber,
    string ProtectedRawToken,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "PaymentLinkNotificationRequestedV1";
    public const string SchemaVersion = "v1";
}
