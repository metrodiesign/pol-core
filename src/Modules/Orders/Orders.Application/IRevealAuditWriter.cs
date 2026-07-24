namespace Orders.Application;

/// <summary>
/// Tracks one <see cref="Orders.Domain.Items.RevealAudit"/> row for the caller's <c>IUnitOfWork</c> to
/// persist (mirrors <c>IRegistrationAuditWriter.Append</c> — the audit rides the same save as the rest of
/// the request, not its own independent scope; unlike <c>IVaultRevealAuditWriter</c>, nothing here is
/// exposed to memory before the DB write, so there is no durability gap to close). A NEW, separate
/// interface — not <c>IVaultRevealAuditWriter</c> (locked decision, design.md).
/// </summary>
public interface IRevealAuditWriter
{
    Task AppendAsync(Guid orderItemId, Guid merchantId, string actorType, string actorId,
        string correlationId, CancellationToken cancellationToken);
}
