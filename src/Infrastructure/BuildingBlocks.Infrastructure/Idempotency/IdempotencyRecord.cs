namespace BuildingBlocks.Infrastructure.Idempotency;

/// <summary>One claimed idempotency key. The key is the primary key, so a duplicate insert is
/// rejected by the database — that unique violation is how a replay is detected.</summary>
public sealed class IdempotencyRecord
{
    public string Key { get; private set; } = default!;

    /// <summary>The merchant that claimed the key. Claims happen inside the handler AFTER the merchant is
    /// resolved, so this is always the active merchant. The <see cref="Key"/> is the sole primary key, so
    /// callers namespace it before claiming: the Order path prefixes the client key with merchant and
    /// operation, and webhook/checkout claims embed the connection or order id. That is what stops one
    /// merchant from burning another merchant's key; the table is also query-filtered on this column so a
    /// merchant principal cannot read another merchant's rows.</summary>
    public Guid MerchantId { get; private set; }
    public string Context { get; private set; } = default!;
    public DateTime CreatedAt { get; private set; }

    private IdempotencyRecord() { }

    public IdempotencyRecord(string key, Guid merchantId, string context, DateTime createdAt)
    {
        Key = key;
        MerchantId = merchantId;
        Context = context;
        CreatedAt = createdAt;
    }
}
