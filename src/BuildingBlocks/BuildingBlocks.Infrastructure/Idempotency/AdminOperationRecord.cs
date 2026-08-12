namespace BuildingBlocks.Infrastructure.Idempotency;

public enum AdminOperationState
{
    InProgress = 1,
    Succeeded = 2,
    Unknown = 3,
}

/// <summary>MerchantRuntime-owned bounded replay record for Admin mutations.</summary>
public sealed class AdminOperationRecord
{
    public Guid Id { get; private set; }
    public Guid MerchantId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Operation { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public string IntentHash { get; private set; } = default!;
    public AdminOperationState State { get; private set; }
    public int? HttpStatus { get; private set; }
    public string? Result { get; private set; }
    public string? ResourceId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    private AdminOperationRecord() { }

    public static AdminOperationRecord Create(
        Guid merchantId,
        Guid actorId,
        string operation,
        string idempotencyKey,
        string intentHash,
        DateTime now)
    {
        if (merchantId == Guid.Empty || actorId == Guid.Empty)
            throw new ArgumentException("Merchant and actor identifiers are required.");
        return new()
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            ActorId = actorId,
            Operation = Required(operation, nameof(operation), 120),
            IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey), 200),
            IntentHash = Required(intentHash, nameof(intentHash), 64),
            State = AdminOperationState.InProgress,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        };
    }

    public void Succeed(int httpStatus, string? result, string? resourceId)
    {
        if (httpStatus is < 200 or > 599)
            throw new ArgumentOutOfRangeException(nameof(httpStatus));
        HttpStatus = httpStatus;
        Result = Optional(result, 16_384);
        ResourceId = Optional(resourceId, 200);
        State = AdminOperationState.Succeeded;
    }

    public void MarkUnknown(string? resourceId)
    {
        ResourceId = Optional(resourceId, 200);
        State = AdminOperationState.Unknown;
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return trimmed;
    }

    private static string? Optional(string? value, int maxLength) => value is null
        ? null
        : value.Length <= maxLength ? value : throw new ArgumentException($"Value exceeds {maxLength} characters.");
}
