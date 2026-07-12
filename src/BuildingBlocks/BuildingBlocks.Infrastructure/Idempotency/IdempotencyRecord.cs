namespace BuildingBlocks.Infrastructure.Idempotency;

/// <summary>One claimed idempotency key. The key is the primary key, so a duplicate insert is
/// rejected by the database — that unique violation is how a replay is detected.</summary>
public sealed class IdempotencyRecord
{
    public string Key { get; private set; } = default!;

    /// <summary>The merchant that claimed the key. Claims happen inside the webhook handler AFTER the
    /// merchant is resolved, so this is always the active merchant; the table is RLS-filtered on it so a
    /// merchant principal can neither read nor poison another merchant's idempotency keys.</summary>
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
