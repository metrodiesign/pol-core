namespace BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// A pending integration event written in the same transaction as its state change and published
/// later by <see cref="OutboxDispatcher"/>. Lease columns let multiple dispatcher instances poll
/// without double-publishing (PLAN decision #10): a row is claimed by setting an owner + expiry.
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; private set; }
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LeaseExpiresAtUtc { get; private set; }
    public string? LeaseOwner { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage Create(Guid id, string type, string payload, DateTime occurredAtUtc) =>
        new()
        {
            Id = id,
            Type = type,
            Payload = payload,
            OccurredAtUtc = occurredAtUtc,
        };

    public void MarkProcessed(DateTime utcNow)
    {
        ProcessedAtUtc = utcNow;
        Error = null;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }

    public void MarkFailed(string error)
    {
        Error = error;
        LeaseOwner = null;
        LeaseExpiresAtUtc = null;
    }
}
