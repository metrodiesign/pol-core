using SharedKernel;

namespace Merchants.Domain.Users;

public sealed class AdminUserOperationRecord : Entity<Guid>
{
    public Guid? MerchantId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Operation { get; private set; } = default!;
    public string IdempotencyKey { get; private set; } = default!;
    public string IntentHash { get; private set; } = default!;
    public string Result { get; private set; } = default!;
    public int HttpStatus { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ExpiresAt { get; private set; }

    private AdminUserOperationRecord() { }

    public static AdminUserOperationRecord Succeeded(
        Guid? merchantId, Guid actorId, string operation, string idempotencyKey,
        string intentHash, string result, int httpStatus, DateTime now)
    {
        if (merchantId == Guid.Empty || actorId == Guid.Empty)
            throw new ArgumentException("Actor is required and merchant cannot be empty.");
        if (httpStatus is < 200 or > 299)
            throw new ArgumentOutOfRangeException(nameof(httpStatus));
        return new AdminUserOperationRecord
        {
            Id = Guid.CreateVersion7(),
            MerchantId = merchantId,
            ActorId = actorId,
            Operation = Required(operation, nameof(operation), 120),
            IdempotencyKey = Required(idempotencyKey, nameof(idempotencyKey), 200),
            IntentHash = Required(intentHash, nameof(intentHash), 64),
            Result = Required(result, nameof(result), 16_384),
            HttpStatus = httpStatus,
            CreatedAt = now,
            ExpiresAt = now.AddHours(24),
        };
    }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
        return trimmed;
    }
}
