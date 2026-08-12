namespace Notifications.Domain;

public enum DeliveryStatus { Pending = 1, Processing = 2, Delivered = 3, Failed = 4 }
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
