namespace Notifications.Domain;

public enum DeliveryStatus
{
    Pending = 1,
    Processing = 2,
    Delivered = 3,
    Failed = 4,
    Accepted = 5,
    Unknown = 6,
    BlockedNotConfigured = 7,
    ManualQueue = 8,
}
public enum DeliverySecretState { Staged = 1, Active = 2, Retired = 3, Discarded = 4 }

public sealed class WebhookEndpoint
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string Url { get; private set; } = default!;
    public string EventsCsv { get; private set; } = default!;
    public bool Enabled { get; private set; }
    public Guid ActiveSecretVersionId { get; private set; }
    public string SecretHint { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private WebhookEndpoint() { }

    public static WebhookEndpoint Create(Guid merchantId, string name, string url,
        IReadOnlyCollection<string> events, Guid secretVersionId, string hint, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            Name = Required(name),
            Url = Required(url),
            EventsCsv = JoinEvents(events),
            Enabled = true,
            ActiveSecretVersionId = secretVersionId,
            SecretHint = hint,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

    public void Update(string name, string url, IReadOnlyCollection<string> events, bool enabled, DateTime now)
    {
        Name = Required(name); Url = Required(url); EventsCsv = JoinEvents(events);
        Enabled = enabled; UpdatedAt = now; Version++;
    }

    public IReadOnlyList<string> Events() =>
        EventsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string JoinEvents(IReadOnlyCollection<string> events) =>
        string.Join(',', events.Order(StringComparer.Ordinal));

    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
}

public sealed class WebhookDelivery
{
    public const int MaxAttempts = 8;
    public Guid Id { get; private set; }
    public Guid EndpointId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid SourceEventId { get; private set; }
    public Guid? OriginalDeliveryId { get; private set; }
    public string? ReplayKey { get; private set; }
    public string EventType { get; private set; } = default!;
    public string? TransactionId { get; private set; }
    public string Payload { get; private set; } = default!;
    public DeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime NextAttemptAt { get; private set; }
    public DateTime? LastAttemptAt { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }
    public int? LatencyMs { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private WebhookDelivery() { }

    public static WebhookDelivery Create(Guid endpointId, Guid merchantId, Guid sourceEventId,
        string eventType, string? transactionId, string payload, DateTime now) => new()
        {
            Id = Guid.CreateVersion7(),
            EndpointId = endpointId,
            MerchantId = merchantId,
            SourceEventId = sourceEventId,
            EventType = Required(eventType),
            TransactionId = transactionId,
            Payload = Required(payload),
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };

    public static WebhookDelivery Replay(WebhookDelivery source, string replayKey, DateTime now)
    {
        if (source.Status != DeliveryStatus.Failed)
            throw new InvalidOperationException("Delivery is not replayable.");
        return new WebhookDelivery
        {
            Id = Guid.CreateVersion7(),
            EndpointId = source.EndpointId,
            MerchantId = source.MerchantId,
            SourceEventId = source.SourceEventId,
            OriginalDeliveryId = source.Id,
            ReplayKey = Required(replayKey),
            EventType = source.EventType,
            TransactionId = source.TransactionId,
            Payload = source.Payload,
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        };
    }

    public void Claim(string owner, DateTime now, DateTime leaseUntil)
    {
        if (Status == DeliveryStatus.Processing && LeaseExpiresAt > now)
            throw new InvalidOperationException("Delivery lease is active.");
        if (Status is not (DeliveryStatus.Pending or DeliveryStatus.Processing) || NextAttemptAt > now)
            throw new InvalidOperationException("Delivery is not claimable.");
        Status = DeliveryStatus.Processing;
        AttemptCount++;
        LastAttemptAt = now;
        LeaseOwner = Required(owner);
        LeaseExpiresAt = leaseUntil;
    }

    public void Finish(bool delivered, int latencyMs, string? failureCode, DateTime now, DateTime? retryAt)
    {
        if (Status != DeliveryStatus.Processing) throw new InvalidOperationException("Delivery is not processing.");
        LatencyMs = Math.Max(0, latencyMs);
        LeaseOwner = null;
        LeaseExpiresAt = null;
        if (delivered)
        {
            Status = DeliveryStatus.Delivered;
            FailureCode = null;
            CompletedAt = now;
            return;
        }
        FailureCode = Required(failureCode ?? "delivery_failed");
        if (AttemptCount >= MaxAttempts || retryAt is null)
        {
            Status = DeliveryStatus.Failed;
            CompletedAt = now;
            return;
        }
        Status = DeliveryStatus.Pending;
        NextAttemptAt = retryAt.Value;
    }

    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
}

public sealed class NotificationRule
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public string EventType { get; private set; } = default!;
    public string Channel { get; private set; } = default!;
    public string Destination { get; private set; } = default!;
    public string? Threshold { get; private set; }
    public bool Enabled { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public long Version { get; private set; }

    private NotificationRule() { }

    public static NotificationRule Create(Guid merchantId, string eventType, string channel, string destination,
        string? threshold, bool enabled, DateTime now) => new()
        {
            Id = Guid.NewGuid(),
            MerchantId = merchantId,
            EventType = Required(eventType),
            Channel = Required(channel),
            Destination = Required(destination),
            Threshold = Normalize(threshold),
            Enabled = enabled,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

    public void Update(string eventType, string channel, string destination, string? threshold, bool enabled, DateTime now)
    {
        EventType = Required(eventType); Channel = Required(channel); Destination = Required(destination);
        Threshold = Normalize(threshold); Enabled = enabled; UpdatedAt = now; Version++;
    }

    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class NotificationDelivery
{
    public Guid Id { get; private set; }
    public Guid RuleId { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid SourceEventId { get; private set; }
    public string EventType { get; private set; } = default!;
    public string Channel { get; private set; } = default!;
    public string DestinationMasked { get; private set; } = default!;
    public DeliveryStatus Status { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTime SentAt { get; private set; }

    private NotificationDelivery() { }

    public static NotificationDelivery Record(NotificationRule rule, Guid sourceEventId,
        string destinationMasked, bool delivered, string? failureCode, DateTime now) => new()
        {
            Id = Guid.CreateVersion7(),
            RuleId = rule.Id,
            MerchantId = rule.MerchantId,
            SourceEventId = sourceEventId,
            EventType = rule.EventType,
            Channel = rule.Channel,
            DestinationMasked = destinationMasked,
            Status = delivered ? DeliveryStatus.Delivered : DeliveryStatus.Failed,
            FailureCode = delivered ? null : failureCode ?? "delivery_failed",
            SentAt = now,
        };
}

public sealed class DeliverySecretVersion
{
    public Guid Id { get; private set; }
    public Guid OwnerId { get; private set; }
    public Guid MerchantId { get; private set; }
    public string OwnerType { get; private set; } = default!;
    public string ProtectedSecret { get; private set; } = default!;
    public DeliverySecretState State { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ActivatedAt { get; private set; }
    public DateTime? RetiredAt { get; private set; }

    private DeliverySecretVersion() { }

    public static DeliverySecretVersion Stage(Guid id, Guid ownerId, Guid merchantId,
        string ownerType, string protectedSecret, DateTime now) => new()
        {
            Id = id,
            OwnerId = ownerId,
            MerchantId = merchantId,
            OwnerType = ownerType,
            ProtectedSecret = protectedSecret,
            State = DeliverySecretState.Staged,
            CreatedAt = now,
        };

    public void Activate(DateTime now)
    {
        if (State != DeliverySecretState.Staged) throw new InvalidOperationException("Secret is not staged.");
        State = DeliverySecretState.Active; ActivatedAt = now;
    }

    public void Retire(DateTime now)
    {
        if (State != DeliverySecretState.Active) throw new InvalidOperationException("Secret is not active.");
        State = DeliverySecretState.Retired; RetiredAt = now;
    }

    public void Discard(DateTime now)
    {
        if (State != DeliverySecretState.Staged) throw new InvalidOperationException("Secret is not staged.");
        State = DeliverySecretState.Discarded; RetiredAt = now;
    }
}

/// <summary>One durable notification created from one source event.</summary>
public sealed class Notification
{
    public Guid Id { get; private set; }
    public Guid SourceEventId { get; private set; }
    public Guid MerchantId { get; private set; }
    public string EventType { get; private set; } = default!;
    public string PayloadSnapshot { get; private set; } = default!;
    public string? CorrelationId { get; private set; }
    public Guid? RegistrationId { get; private set; }
    public Guid? RegistrationAttemptId { get; private set; }
    public Guid? OrderId { get; private set; }
    public string? OrderNo { get; private set; }
    public Guid? TransactionId { get; private set; }
    public string? TransactionNo { get; private set; }
    public DateTime OccurredAt { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private Notification() { }

    public static Notification Create(
        Guid sourceEventId,
        Guid merchantId,
        string eventType,
        string payloadSnapshot,
        DateTime occurredAt,
        string? correlationId = null,
        Guid? registrationId = null,
        Guid? registrationAttemptId = null,
        Guid? orderId = null,
        string? orderNo = null,
        Guid? transactionId = null,
        string? transactionNo = null) => new()
        {
            Id = Guid.CreateVersion7(),
            SourceEventId = Required(sourceEventId, nameof(sourceEventId)),
            MerchantId = Required(merchantId, nameof(merchantId)),
            EventType = Required(eventType),
            PayloadSnapshot = Required(payloadSnapshot),
            CorrelationId = Normalize(correlationId),
            RegistrationId = Optional(registrationId),
            RegistrationAttemptId = Optional(registrationAttemptId),
            OrderId = Optional(orderId),
            OrderNo = Normalize(orderNo),
            TransactionId = Optional(transactionId),
            TransactionNo = Normalize(transactionNo),
            OccurredAt = occurredAt,
            CreatedAt = occurredAt,
        };

    private static Guid Required(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("Identifier is required.", name) : value;
    private static Guid? Optional(Guid? value) => value == Guid.Empty ? null : value;
    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Release-owned template row. It has no update operation by design.</summary>
public sealed class TemplateVersion
{
    public Guid Id { get; private set; }
    public string EventType { get; private set; } = default!;
    public string Channel { get; private set; } = default!;
    public string Version { get; private set; } = default!;
    public string Locale { get; private set; } = default!;
    public string Subject { get; private set; } = default!;
    public string Content { get; private set; } = default!;
    public DateTime ReleasedAt { get; private set; }

    private TemplateVersion() { }

    public static TemplateVersion Release(
        string eventType, string channel, string version, string locale,
        string subject, string content, DateTime releasedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            EventType = Required(eventType),
            Channel = Required(channel),
            Version = Required(version),
            Locale = Required(locale),
            Subject = subject?.Trim() ?? string.Empty,
            Content = Required(content),
            ReleasedAt = releasedAt,
        };

    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
}

/// <summary>Commerce delivery with immutable recipient, endpoint and template snapshots.</summary>
public sealed class Delivery
{
    public const int MaxAttempts = 5;

    public Guid Id { get; private set; }
    public Guid NotificationId { get; private set; }
    public Guid SourceEventId { get; private set; }
    public Guid MerchantId { get; private set; }
    public string Channel { get; private set; } = default!;
    public string RecipientSnapshot { get; private set; } = default!;
    public string RecipientFingerprint { get; private set; } = default!;
    public Guid TemplateVersionId { get; private set; }
    public string TemplateVersion { get; private set; } = default!;
    public string TemplateLocale { get; private set; } = default!;
    public string TemplateSubjectSnapshot { get; private set; } = default!;
    public string TemplateContentSnapshot { get; private set; } = default!;
    public string? EndpointUrlSnapshot { get; private set; }
    public string? ProtectedEndpointSecretSnapshot { get; private set; }
    public string PayloadSnapshot { get; private set; } = default!;
    public DeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTime NextAttemptAt { get; private set; }
    public DateTime? LastAttemptAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? FailureCode { get; private set; }

    private Delivery() { }

    public static Delivery Create(
        Guid notificationId,
        Guid sourceEventId,
        Guid merchantId,
        string channel,
        string recipientSnapshot,
        TemplateVersion template,
        string payloadSnapshot,
        DateTime now,
        string? endpointUrlSnapshot = null,
        string? protectedEndpointSecretSnapshot = null) => new()
        {
            Id = Guid.CreateVersion7(),
            NotificationId = Required(notificationId, nameof(notificationId)),
            SourceEventId = Required(sourceEventId, nameof(sourceEventId)),
            MerchantId = Required(merchantId, nameof(merchantId)),
            Channel = Required(channel),
            RecipientSnapshot = Required(recipientSnapshot),
            RecipientFingerprint = Fingerprint(recipientSnapshot),
            TemplateVersionId = Required(template.Id, nameof(template)),
            TemplateVersion = Required(template.Version),
            TemplateLocale = Required(template.Locale),
            TemplateSubjectSnapshot = template.Subject,
            TemplateContentSnapshot = Required(template.Content),
            EndpointUrlSnapshot = Normalize(endpointUrlSnapshot),
            ProtectedEndpointSecretSnapshot = Normalize(protectedEndpointSecretSnapshot),
            PayloadSnapshot = Required(payloadSnapshot),
            Status = DeliveryStatus.Pending,
            NextAttemptAt = now,
        };

    public void Claim(string owner, DateTime now, DateTime leaseUntil)
    {
        if (Status == DeliveryStatus.Processing && LeaseExpiresAt > now)
            throw new InvalidOperationException("Delivery lease is active.");
        if (Status is not (DeliveryStatus.Pending or DeliveryStatus.Unknown or DeliveryStatus.Processing)
            || NextAttemptAt > now)
            throw new InvalidOperationException("Delivery is not claimable.");
        Status = DeliveryStatus.Processing;
        AttemptCount++;
        LastAttemptAt = now;
        LeaseOwner = Required(owner);
        LeaseExpiresAt = leaseUntil;
        FailureCode = null;
    }

    public void MarkAccepted(string? providerMessageId, DateTime now)
    {
        EnsureProcessingOrUnknown();
        Status = DeliveryStatus.Accepted;
        ProviderMessageId = Normalize(providerMessageId);
        LeaseOwner = null;
        LeaseExpiresAt = null;
        CompletedAt = null;
    }

    public void MarkDelivered(string? providerMessageId, DateTime now)
    {
        if (Status is not (DeliveryStatus.Processing or DeliveryStatus.Accepted or DeliveryStatus.Unknown))
            throw new InvalidOperationException("Delivery cannot be marked delivered from its current state.");
        Status = DeliveryStatus.Delivered;
        ProviderMessageId = Normalize(providerMessageId) ?? ProviderMessageId;
        LeaseOwner = null;
        LeaseExpiresAt = null;
        CompletedAt = now;
        FailureCode = null;
    }

    public void MarkUnknown(string failureCode, DateTime now, DateTime? retryAt)
    {
        EnsureProcessingOrUnknown();
        FailureCode = Required(failureCode);
        LeaseOwner = null;
        LeaseExpiresAt = null;
        if (retryAt is { } next && AttemptCount < MaxAttempts)
        {
            Status = DeliveryStatus.Unknown;
            NextAttemptAt = next;
            return;
        }
        Status = DeliveryStatus.ManualQueue;
        CompletedAt = now;
    }

    public void MarkFailed(string failureCode, DateTime now, DateTime? retryAt)
    {
        EnsureProcessingOrUnknown();
        FailureCode = Required(failureCode);
        LeaseOwner = null;
        LeaseExpiresAt = null;
        if (retryAt is { } next && AttemptCount < MaxAttempts)
        {
            Status = DeliveryStatus.Pending;
            NextAttemptAt = next;
            return;
        }
        Status = DeliveryStatus.ManualQueue;
        CompletedAt = now;
    }

    public void BlockNotConfigured(string reason, DateTime now)
    {
        if (Status is not (DeliveryStatus.Pending or DeliveryStatus.Unknown))
            throw new InvalidOperationException("Only an unclaimed delivery can be blocked.");
        Status = DeliveryStatus.BlockedNotConfigured;
        FailureCode = Required(reason);
        LeaseOwner = null;
        LeaseExpiresAt = null;
        CompletedAt = now;
    }

    public void Retry(DateTime now)
    {
        if (Status is not (DeliveryStatus.Failed or DeliveryStatus.ManualQueue or DeliveryStatus.Unknown
            or DeliveryStatus.BlockedNotConfigured))
            throw new InvalidOperationException("Delivery is not retryable.");
        Status = DeliveryStatus.Pending;
        NextAttemptAt = now;
        CompletedAt = null;
        FailureCode = null;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    public static TimeSpan? RetryDelay(int attemptNumber) => attemptNumber switch
    {
        1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromMinutes(60),
        5 => TimeSpan.FromMinutes(240),
        _ => null,
    };

    public static string Fingerprint(string recipient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(recipient.Trim().ToUpperInvariant()))).ToLowerInvariant();
    }

    private void EnsureProcessingOrUnknown()
    {
        if (Status is not (DeliveryStatus.Processing or DeliveryStatus.Accepted or DeliveryStatus.Unknown))
            throw new InvalidOperationException("Delivery is not in a provider-result state.");
    }

    private static Guid Required(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("Identifier is required.", name) : value;
    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Append-only provider attempt; retry never mutates this history.</summary>
public sealed class DeliveryAttempt
{
    public Guid Id { get; private set; }
    public Guid DeliveryId { get; private set; }
    public Guid MerchantId { get; private set; }
    public int AttemptNo { get; private set; }
    public string Outcome { get; private set; } = default!;
    public string? ProviderMessageId { get; private set; }
    public string? FailureCode { get; private set; }
    public int? LatencyMs { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime CompletedAt { get; private set; }

    private DeliveryAttempt() { }

    public static DeliveryAttempt Record(
        Guid deliveryId, Guid merchantId, int attemptNo, string outcome,
        DateTime startedAt, DateTime completedAt, string? providerMessageId = null,
        string? failureCode = null, int? latencyMs = null) => new()
        {
            Id = Guid.CreateVersion7(),
            DeliveryId = Required(deliveryId, nameof(deliveryId)),
            MerchantId = Required(merchantId, nameof(merchantId)),
            AttemptNo = attemptNo <= 0 ? throw new ArgumentOutOfRangeException(nameof(attemptNo)) : attemptNo,
            Outcome = Required(outcome),
            ProviderMessageId = Normalize(providerMessageId),
            FailureCode = Normalize(failureCode),
            LatencyMs = latencyMs is null ? null : Math.Max(0, latencyMs.Value),
            StartedAt = startedAt,
            CompletedAt = completedAt,
        };

    private static Guid Required(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("Identifier is required.", name) : value;
    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Durable dedupe marker for a source event before Notification fan-out.</summary>
public sealed class NotificationInboxMessage
{
    public Guid Id { get; private set; }
    public Guid SourceEventId { get; private set; }
    public Guid MerchantId { get; private set; }
    public string EventType { get; private set; } = default!;
    public string PayloadSnapshot { get; private set; } = default!;
    public DateTime ReceivedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }

    private NotificationInboxMessage() { }

    public static NotificationInboxMessage Receive(
        Guid sourceEventId, Guid merchantId, string eventType, string payloadSnapshot, DateTime receivedAt) => new()
        {
            Id = Guid.CreateVersion7(),
            SourceEventId = Required(sourceEventId, nameof(sourceEventId)),
            MerchantId = Required(merchantId, nameof(merchantId)),
            EventType = Required(eventType),
            PayloadSnapshot = Required(payloadSnapshot),
            ReceivedAt = receivedAt,
        };

    public void MarkProcessed(DateTime now) => ProcessedAt = now;

    private static Guid Required(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("Identifier is required.", name) : value;
    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
}

/// <summary>Append-only operator note. It has no financial mutation capability.</summary>
public sealed class NotificationReviewNote
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid? NotificationId { get; private set; }
    public Guid? DeliveryId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Note { get; private set; } = default!;
    public string? CorrelationId { get; private set; }
    public DateTime CreatedAt { get; private set; }

    private NotificationReviewNote() { }

    public static NotificationReviewNote Add(
        Guid merchantId, Guid actorId, string note, DateTime createdAt,
        Guid? notificationId = null, Guid? deliveryId = null, string? correlationId = null) => new()
        {
            Id = Guid.CreateVersion7(),
            MerchantId = Required(merchantId, nameof(merchantId)),
            ActorId = Required(actorId, nameof(actorId)),
            Note = Required(note),
            NotificationId = Optional(notificationId),
            DeliveryId = Optional(deliveryId),
            CorrelationId = Normalize(correlationId),
            CreatedAt = createdAt,
        };

    private static Guid Required(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("Identifier is required.", name) : value;
    private static Guid? Optional(Guid? value) => value == Guid.Empty ? null : value;
    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
