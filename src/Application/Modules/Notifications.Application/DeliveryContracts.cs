using BuildingBlocks.Application;

namespace Notifications.Application;

public sealed record NotificationEvent(
    Guid SourceEventId,
    Guid MerchantId,
    string EventType,
    string PayloadSnapshot,
    DateTime OccurredAt,
    string? Email = null,
    string? PhoneNumber = null,
    Guid? RegistrationId = null,
    Guid? RegistrationAttemptId = null,
    Guid? OrderId = null,
    string? OrderNo = null,
    Guid? TransactionId = null,
    string? TransactionNo = null,
    string? CorrelationId = null);

/// <summary>Handoff from a control/runtime outbox consumer to Commerce-owned notification state.</summary>
public interface INotificationMaterializer
{
    Task MaterializeAsync(NotificationEvent notification, CancellationToken cancellationToken);
}

public enum DeliveryProviderOutcome { Accepted, Delivered, Failed, Unknown, BlockedNotConfigured }

public sealed record DeliveryProviderResult(
    DeliveryProviderOutcome Outcome,
    string? ProviderMessageId = null,
    string? FailureCode = null);

public enum NotificationReceiptOutcome { Accepted, Delivered, Failed, Unknown }

/// <summary>Provider receipt after signature/authentication verification. The verifier owns provider-specific parsing;
/// the application only receives a bounded delivery id, outcome and provider reference.</summary>
public sealed record NotificationReceipt(
    Guid DeliveryId,
    NotificationReceiptOutcome Outcome,
    string ProviderMessageId,
    string? FailureCode = null);

public sealed record NotificationReceiptVerification(
    bool IsValid,
    string? Code,
    NotificationReceipt? Receipt)
{
    public static NotificationReceiptVerification Invalid(string code) => new(false, code, null);
    public static NotificationReceiptVerification Valid(NotificationReceipt receipt) => new(true, null, receipt);
}

public interface INotificationReceiptVerifier
{
    Task<NotificationReceiptVerification> VerifyAsync(
        string providerCode, string rawPayload, string signature, CancellationToken cancellationToken);
}

/// <summary>Fail-closed default until a provider-specific receipt verifier is configured.</summary>
public sealed class NoVendorNotificationReceiptVerifier : INotificationReceiptVerifier
{
    public Task<NotificationReceiptVerification> VerifyAsync(
        string providerCode, string rawPayload, string signature, CancellationToken cancellationToken) =>
        Task.FromResult(NotificationReceiptVerification.Invalid("notification_provider_not_configured"));
}

public sealed record EmailDeliveryRequest(
    string Recipient,
    string Subject,
    string Body,
    string TemplateVersion,
    Guid DeliveryId);

public sealed record SmsDeliveryRequest(
    string Recipient,
    string Body,
    string TemplateVersion,
    Guid DeliveryId);

public interface IEmailSenderPort
{
    Task<DeliveryProviderResult> SendAsync(
        EmailDeliveryRequest request, CancellationToken cancellationToken);
}

public interface ISmsSenderPort
{
    bool IsConfigured { get; }

    Task<DeliveryProviderResult> SendAsync(
        SmsDeliveryRequest request, CancellationToken cancellationToken);
}

public sealed class NotConfiguredSmsSender : ISmsSenderPort
{
    public bool IsConfigured => false;

    public Task<DeliveryProviderResult> SendAsync(
        SmsDeliveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new DeliveryProviderResult(
            DeliveryProviderOutcome.BlockedNotConfigured,
            FailureCode: "sms_not_configured"));
}

public sealed class DeliveryProviderAmbiguousException : Exception
{
    public DeliveryProviderAmbiguousException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public sealed record BusinessWebhookDeliveryRequest(
    Guid DeliveryId,
    string Url,
    string ProtectedSecret,
    string Payload);

public interface IBusinessWebhookSender
{
    Task<DeliveryProviderResult> SendAsync(
        BusinessWebhookDeliveryRequest request, CancellationToken cancellationToken);
}

public sealed record BusinessWebhookConfiguration(
    Guid EndpointId,
    string Url,
    string ProtectedSecret);

/// <summary>Reads the control-plane endpoint without moving its ownership into Commerce.</summary>
public interface IBusinessWebhookConfigurationReader
{
    Task<BusinessWebhookConfiguration?> GetAsync(
        Guid merchantId, string eventType, CancellationToken cancellationToken);
}

public sealed record NotificationSearchQuery(
    int Page,
    int Limit,
    Guid? MerchantId = null,
    string? OrderNo = null,
    string? TransactionNo = null,
    string? CorrelationId = null);

public sealed record CommerceNotificationView(
    Guid Id,
    Guid SourceEventId,
    Guid MerchantId,
    string EventType,
    Guid? RegistrationId,
    Guid? RegistrationAttemptId,
    Guid? OrderId,
    string? OrderNo,
    Guid? TransactionId,
    string? TransactionNo,
    string? CorrelationId,
    DateTime OccurredAt,
    DateTime CreatedAt);

public sealed record CommerceDeliveryView(
    Guid Id,
    Guid NotificationId,
    Guid MerchantId,
    string Channel,
    string RecipientMasked,
    string TemplateVersion,
    string TemplateLocale,
    string Status,
    int AttemptCount,
    DateTime NextAttemptAt,
    DateTime? CompletedAt,
    string? ProviderMessageId,
    string? FailureCode);

public sealed record CommerceDeliveryAttemptView(
    Guid Id,
    Guid DeliveryId,
    int AttemptNo,
    string Outcome,
    string? ProviderMessageId,
    string? FailureCode,
    int? LatencyMs,
    DateTime StartedAt,
    DateTime CompletedAt);

public sealed record NotificationReceiptView(
    Guid DeliveryId,
    string Status,
    string? ProviderMessageId,
    bool Replayed);

public sealed record NotificationReviewNoteView(
    Guid Id,
    Guid MerchantId,
    Guid? NotificationId,
    Guid? DeliveryId,
    Guid ActorId,
    string Note,
    string? CorrelationId,
    DateTime CreatedAt);

public interface INotificationOperations
{
    Task<PagedResult<CommerceNotificationView>> SearchAsync(
        NotificationSearchQuery query, DeliveryAccess access, CancellationToken cancellationToken);
    Task<CommerceNotificationView?> GetAsync(
        Guid notificationId, DeliveryAccess access, CancellationToken cancellationToken);
    Task<CommerceDeliveryView?> GetDeliveryAsync(
        Guid deliveryId, DeliveryAccess access, CancellationToken cancellationToken);
    Task<IReadOnlyList<CommerceDeliveryAttemptView>> ListAttemptsAsync(
        Guid deliveryId, DeliveryAccess access, CancellationToken cancellationToken);
    Task<CommerceDeliveryView?> RetryAsync(
        Guid deliveryId, Guid actorId, string reason, string idempotencyKey,
        DeliveryAccess access, CancellationToken cancellationToken);
    Task<NotificationReceiptView?> ApplyReceiptAsync(
        NotificationReceipt receipt, CancellationToken cancellationToken);
    Task<Guid?> ResolveDeliveryMerchantAsync(
        Guid deliveryId, CancellationToken cancellationToken);
    Task<NotificationReviewNoteView> AddReviewNoteAsync(
        Guid merchantId, Guid? notificationId, Guid? deliveryId, Guid actorId, string note,
        DeliveryAccess access, CancellationToken cancellationToken);
}

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
