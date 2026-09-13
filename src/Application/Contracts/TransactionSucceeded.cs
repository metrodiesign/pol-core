using Mediator;
using SharedKernel;

namespace Contracts;

/// <summary>Verified Transaction success emitted once for the canonical financial transition.</summary>
public sealed record TransactionSucceeded(
    Guid EventId,
    Guid TransactionId,
    Guid OrderId,
    Guid MerchantId,
    Money Amount,
    string PaymentMethod,
    string ProviderCode,
    string ProviderReference,
    bool NeedsReview,
    DateTime OccurredAt) : INotification
{
    public const string EventType = "payments.transaction-succeeded.v1";
    public const string SchemaVersion = "v1";
}
