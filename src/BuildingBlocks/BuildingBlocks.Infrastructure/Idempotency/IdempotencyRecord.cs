namespace BuildingBlocks.Infrastructure.Idempotency;

/// <summary>One claimed idempotency key. The key is the primary key, so a duplicate insert is
/// rejected by the database — that unique violation is how a replay is detected.</summary>
public sealed class IdempotencyRecord
{
    public string Key { get; private set; } = default!;

    /// <summary>The tenant that claimed the key. Claims happen inside the webhook handler AFTER the
    /// tenant is resolved, so this is always the active tenant; the table is RLS-filtered on it so a
    /// tenant principal can neither read nor poison another tenant's idempotency keys.</summary>
    public Guid TenantId { get; private set; }
    public string Context { get; private set; } = default!;
    public DateTime CreatedAtUtc { get; private set; }

    private IdempotencyRecord() { }

    public IdempotencyRecord(string key, Guid tenantId, string context, DateTime createdAtUtc)
    {
        Key = key;
        TenantId = tenantId;
        Context = context;
        CreatedAtUtc = createdAtUtc;
    }
}
