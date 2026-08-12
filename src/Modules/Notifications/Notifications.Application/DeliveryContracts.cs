using BuildingBlocks.Application;

namespace Notifications.Application;

public sealed record DeliveryAccess(bool IsUnrestricted, IReadOnlySet<Guid> MerchantIds)
{
    public bool Allows(Guid merchantId) => IsUnrestricted || MerchantIds.Contains(merchantId);
}

public sealed record WebhookEndpointView(Guid Id, Guid MerchantId, string Name, string Url,
    IReadOnlyList<string> Events, bool Enabled, string SecretHint, DateTime CreatedAt, DateTime UpdatedAt, long Version);
public sealed record WebhookEndpointCreated(WebhookEndpointView Endpoint, string? SigningSecret, bool Replayed);
public sealed record WebhookEndpointMutation(WebhookEndpointView Endpoint, bool Replayed);
public sealed record WebhookDeliveryView(Guid Id, Guid EndpointId, Guid MerchantId, Guid? OriginalDeliveryId,
    string EventType, string? TransactionId, string Status, int AttemptCount, int? LatencyMs,
    string? FailureCode, DateTime CreatedAt, DateTime? CompletedAt, bool ReplayEligible);
public sealed record WebhookReplayResult(WebhookDeliveryView Delivery, bool Replayed);
public sealed record NotificationRuleView(Guid Id, Guid MerchantId, string EventType, string Channel,
    string Destination, string? Threshold, bool Enabled, DateTime CreatedAt, DateTime UpdatedAt, long Version);
public sealed record NotificationRuleMutation(NotificationRuleView Rule, bool Replayed);
public sealed record NotificationDeliveryView(Guid Id, Guid RuleId, Guid MerchantId, string EventType,
    string Channel, string Destination, string Status, string? FailureCode, DateTime SentAt);

public sealed record WebhookEndpointQuery(int Page, int Limit, Guid? MerchantId, bool? Enabled, string? Search);
public sealed record WebhookDeliveryQuery(int Page, int Limit, Guid? MerchantId, string? Status, string? Search);
public sealed record NotificationRuleQuery(int Page, int Limit, Guid? MerchantId, bool? Enabled, string? Search);
public sealed record NotificationDeliveryQuery(
    int Page, int Limit, Guid? MerchantId, string? Channel, string? Status, string? Search);

public interface IDeliveryControlStore
{
    Task<PagedResult<WebhookEndpointView>> ListEndpointsAsync(
        WebhookEndpointQuery query, DeliveryAccess access, CancellationToken cancellationToken);
    Task<WebhookEndpointView?> GetEndpointAsync(Guid id, DeliveryAccess access, CancellationToken cancellationToken);
    Task<WebhookEndpointCreated> CreateEndpointAsync(Guid merchantId, string name, string url,
        IReadOnlyList<string> events, Guid actorId, string idempotencyKey, DeliveryAccess access,
        CancellationToken cancellationToken);
    Task<WebhookEndpointMutation?> UpdateEndpointAsync(Guid id, string name, string url,
        IReadOnlyList<string> events, bool enabled, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken);
    Task<bool?> DeleteEndpointAsync(Guid id, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken);
    Task<PagedResult<WebhookDeliveryView>> ListWebhookDeliveriesAsync(
        WebhookDeliveryQuery query, DeliveryAccess access, CancellationToken cancellationToken);
    Task<WebhookDeliveryView?> GetWebhookDeliveryAsync(Guid id, DeliveryAccess access, CancellationToken cancellationToken);
    Task<WebhookReplayResult?> ReplayAsync(Guid id, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken);
    Task<PagedResult<NotificationRuleView>> ListRulesAsync(
        NotificationRuleQuery query, DeliveryAccess access, CancellationToken cancellationToken);
    Task<NotificationRuleView?> GetRuleAsync(Guid id, DeliveryAccess access, CancellationToken cancellationToken);
    Task<NotificationRuleMutation> CreateRuleAsync(Guid merchantId, string eventType, string channel,
        string destination, string? threshold, bool enabled, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken);
    Task<NotificationRuleMutation?> UpdateRuleAsync(Guid id, string eventType, string channel,
        string destination, string? threshold, bool enabled, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken);
    Task<bool?> DeleteRuleAsync(Guid id, long version, Guid actorId, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken);
    Task<PagedResult<NotificationDeliveryView>> ListNotificationDeliveriesAsync(
        NotificationDeliveryQuery query, DeliveryAccess access, CancellationToken cancellationToken);
    Task<NotificationDeliveryView?> GetNotificationDeliveryAsync(
        Guid id, DeliveryAccess access, CancellationToken cancellationToken);
}

public interface IDeliveryEventSink
{
    Task EnqueueAsync(Guid sourceEventId, Guid merchantId, string eventType,
        string? transactionId, string payload, CancellationToken cancellationToken);
}

public sealed record ValidatedDestination(Uri Uri, System.Net.IPAddress Address);
public interface ISafeDestinationValidator
{
    Task<ValidatedDestination> ResolveAsync(string url, CancellationToken cancellationToken);
}
