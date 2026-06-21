namespace BuildingBlocks.Infrastructure.Idempotency;

/// <summary>One claimed idempotency key. The key is the primary key, so a duplicate insert is
/// rejected by the database — that unique violation is how a replay is detected.</summary>
public sealed class IdempotencyRecord
{
    public string Key { get; private set; } = default!;
    public string Context { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }

    private IdempotencyRecord() { }

    public IdempotencyRecord(string key, string context, DateTime createdAtUtc)
    {
        Key = key;
        Context = context;
        CreatedAtUtc = createdAtUtc;
    }
}
