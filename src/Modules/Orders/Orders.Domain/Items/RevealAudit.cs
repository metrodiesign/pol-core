using SharedKernel;

namespace Orders.Domain.Lines;

/// <summary>
/// Append-only fact that one <see cref="Line"/>'s full insured-person data was revealed to a merchant-user
/// (REQ-7.5). One row per line actually revealed — a detail read of an N-line order writes N rows.
/// Mirrors <c>Merchants.Domain.Users.RegistrationAudit</c>'s plain shape (actor/target/timestamp), NOT
/// <c>VaultRevealAudit</c>'s SHA-256 hash chain — insured-person PII was not raised to that sensitivity
/// tier (design.md Technology Decisions). Marked <c>AppendOnlyDescriptor</c> at the runtime EF config so
/// <c>GuardedRuntimeDbContext</c> rejects any Update/Delete against this table.
/// </summary>
public sealed class RevealAudit : Entity<Guid>
{
    public Guid OrderLineId { get; private set; }
    public Guid MerchantId { get; private set; }

    /// <summary>"admin" | "merchant-user" — only "merchant-user" is ever written by this spec's one
    /// detail-read endpoint (admin reads are out of scope, see requirements.md non-goals).</summary>
    public string ActorType { get; private set; } = default!;

    public string ActorId { get; private set; } = default!;
    public string CorrelationId { get; private set; } = default!;
    public DateTime RevealedAt { get; private set; }

    private RevealAudit() { }

    private RevealAudit(Guid id, Guid orderLineId, Guid merchantId, string actorType, string actorId,
        string correlationId, DateTime revealedAt) : base(id)
    {
        OrderLineId = orderLineId;
        MerchantId = merchantId;
        ActorType = actorType;
        ActorId = actorId;
        CorrelationId = correlationId;
        RevealedAt = revealedAt;
    }

    public static RevealAudit For(Guid orderLineId, Guid merchantId, string actorType, string actorId,
        string correlationId, DateTime revealedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        return new RevealAudit(Guid.NewGuid(), orderLineId, merchantId, actorType, actorId, correlationId, revealedAt);
    }
}
