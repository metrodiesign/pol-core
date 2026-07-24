using SharedKernel;

namespace Orders.Domain.Items;

/// <summary>
/// Append-only fact that one <see cref="ItemPolicy"/> write happened (REQ-3.5) — one row per successful
/// <c>Apply</c>, whether it created the record or edited an existing one. Mirrors <see cref="RevealAudit"/>'s
/// plain shape (actor/target/timestamp), not a hash chain — same reasoning (design.md: this data carries no
/// PII, and reference numbers are not secret, REQ-4.6). <see cref="ChangeSummary"/> is a comma-list of
/// changed FIELD NAMES only (never values) so the row itself never needs redaction. Marked
/// <c>AppendOnlyDescriptor</c> at the runtime EF config so <c>GuardedRuntimeDbContext</c> rejects any
/// Update/Delete against this table.
/// </summary>
public sealed class ItemPolicyAudit : Entity<Guid>
{
    public Guid OrderItemId { get; private set; }
    public Guid MerchantId { get; private set; }
    public string ActorId { get; private set; } = default!;
    public ActorKind ActorKind { get; private set; }
    public AuditOperation Operation { get; private set; }

    /// <summary>Comma-separated <see cref="ItemPolicy"/> property names that changed this write — never a
    /// value. Empty when a write landed with no field actually different from its prior state (REQ-3.5 audits
    /// every write attempt, not only ones that changed something).</summary>
    public string ChangeSummary { get; private set; } = default!;

    public string CorrelationId { get; private set; } = default!;
    public DateTime OccurredAt { get; private set; }

    private ItemPolicyAudit() { }

    private ItemPolicyAudit(Guid id, Guid orderItemId, Guid merchantId, string actorId, ActorKind actorKind,
        AuditOperation operation, string changeSummary, string correlationId, DateTime occurredAt) : base(id)
    {
        OrderItemId = orderItemId;
        MerchantId = merchantId;
        ActorId = actorId;
        ActorKind = actorKind;
        Operation = operation;
        ChangeSummary = changeSummary;
        CorrelationId = correlationId;
        OccurredAt = occurredAt;
    }

    public static ItemPolicyAudit For(Guid orderItemId, Guid merchantId, string actorId, ActorKind actorKind,
        AuditOperation operation, string changeSummary, string correlationId, DateTime occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(changeSummary);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new ItemPolicyAudit(
            Guid.NewGuid(), orderItemId, merchantId, actorId, actorKind, operation, changeSummary, correlationId,
            occurredAt);
    }
}
