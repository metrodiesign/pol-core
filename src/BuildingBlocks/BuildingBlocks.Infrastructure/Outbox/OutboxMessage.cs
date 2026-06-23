namespace BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// A pending integration event written in the same transaction as its state change and published
/// later by <see cref="OutboxDispatcher"/>. Lease columns let multiple dispatcher instances poll
/// without double-publishing (PLAN decision #10): a row is claimed by setting an owner + expiry.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }

    /// <summary>
    /// The tenant the event belongs to, taken from the writer's tenant context at enqueue. The
    /// producer table carries a BLOCK-after-insert RLS predicate, so a tenant principal can only
    /// insert a row whose <c>TenantId</c> matches its own <c>SESSION_CONTEXT</c> — it cannot forge
    /// another tenant's id. The dispatcher trusts this value to re-establish the tenant context
    /// before invoking in-process consumers (PLAN decision #3, #10).
    /// </summary>
    public Guid TenantId { get; private set; }
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create(Guid id, Guid tenantId, string type, string payload, DateTime occurredAt) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            Type = type,
            Payload = payload,
            OccurredAt = occurredAt,
        };

    public void MarkProcessed(DateTime utcNow)
    {
        ProcessedAt = utcNow;
        Error = null;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }

    public void MarkFailed(string error)
    {
        Error = error;
        LeaseOwner = null;
        LeaseExpiresAt = null;
    }
}
