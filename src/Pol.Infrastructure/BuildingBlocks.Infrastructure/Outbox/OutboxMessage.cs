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
    /// The merchant the event belongs to, taken from the writer's actor context at enqueue. The
    /// table carries a BLOCK-after-insert RLS predicate, so a merchant principal can only insert a
    /// row whose <c>MerchantId</c> matches its own <c>SESSION_CONTEXT</c> — it cannot forge another
    /// merchant's id. The dispatcher trusts this value to re-establish the actor context before
    /// invoking in-process consumers.
    /// </summary>
    public Guid MerchantId { get; private set; }
    public string Type { get; private set; } = default!;
    public string SchemaVersion { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create(
        Guid id,
        Guid merchantId,
        string type,
        string schemaVersion,
        string payload,
        DateTime occurredAt) =>
        new()
        {
            Id = id,
            MerchantId = merchantId,
            Type = type,
            SchemaVersion = schemaVersion,
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
