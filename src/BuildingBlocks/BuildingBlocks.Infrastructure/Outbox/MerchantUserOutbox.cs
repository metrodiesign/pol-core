namespace BuildingBlocks.Infrastructure.Outbox;

/// <summary>
/// rls-to-query-filter task 5 (design.md "Outbox is per-owner with an owner-specific CLR type"): the
/// MerchantUser cluster's own outbox — a distinct CLR type mapped to the NEW <c>merch.UserOutbox</c> table
/// (task 8's migration atomically moves the legacy registration-sentinel rows out of
/// <c>txn.OutboxMessages</c> into this table; sentinel becomes forbidden on <c>txn.OutboxMessages</c>
/// afterward). Same shape as <see cref="OutboxMessage"/> (the MerchantRuntime cluster's outbox, which
/// legitimately reuses that existing type/table — REQ-1.7 draws the line at "one entity per runtime
/// context", not at code duplication) — deliberately NOT shared: sharing one CLR type across two runtime
/// contexts is exactly the "one entity -> one runtime context" violation this split exists to avoid.
/// </summary>
public sealed class MerchantUserOutbox
{
    public Guid Id { get; private set; }

    /// <summary>The owning merchant, or the registration sentinel for a still-pending applicant (no real
    /// merchant exists yet) — mirrors <see cref="OutboxMessage.MerchantId"/>'s trust model.</summary>
    public Guid MerchantId { get; private set; }
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }
    public int Attempts { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LeaseExpiresAt { get; private set; }
    public string? LeaseOwner { get; private set; }

    private MerchantUserOutbox() { }

    public static MerchantUserOutbox Create(Guid id, Guid merchantId, string type, string payload, DateTime occurredAt) =>
        new()
        {
            Id = id,
            MerchantId = merchantId,
            Type = type,
            Payload = payload,
            OccurredAt = occurredAt,
        };

    /// <summary>Claims this row for one dispatcher attempt (mirrors the raw-SQL lease claim
    /// <see cref="OutboxDispatcher"/> issues for <see cref="OutboxMessage"/> — exposed as a method here
    /// because this outbox's drain port updates via tracked EF LINQ, not raw SQL).</summary>
    public void Lease(string owner, DateTime expiresAt)
    {
        LeaseOwner = owner;
        LeaseExpiresAt = expiresAt;
        Attempts++;
    }

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
