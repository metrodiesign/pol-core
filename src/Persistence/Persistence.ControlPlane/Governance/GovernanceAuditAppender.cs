using Governance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Governance;

internal sealed class GovernanceAuditAppender(
    ControlPlaneDbContext db,
    GovernanceSqlLockManager locks)
{
    public async Task AppendAsync(
        GovernanceScopeKind scopeKind,
        Guid? merchantId,
        Guid actorId,
        string action,
        string resourceType,
        string resourceId,
        string result,
        string changes,
        Guid? approvalId,
        string? resourceVersion,
        string correlationId,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        var scopeKey = ScopeKey(scopeKind, merchantId);
        await locks.AcquireAsync($"audit:{scopeKey}", cancellationToken);
        await locks.AcquireAuditHeadAsync(scopeKey, cancellationToken);

        var head = await db.AuditHeads.SingleOrDefaultAsync(x => x.Id == scopeKey, cancellationToken);
        if (head is null)
        {
            head = AuditHead.Create(scopeKey, scopeKind, merchantId, occurredAt);
            db.AuditHeads.Add(head);
        }

        var record = AuditRecord.Append(
            scopeKey, scopeKind, merchantId, head.LastSequence + 1, head.LastHash, actorId,
            action, resourceType, resourceId, result, AuditRedactor.RedactAndCanonicalize(changes),
            approvalId, resourceVersion, correlationId, occurredAt);
        head.Advance(record.Sequence, record.PreviousHash, record.Hash, occurredAt);
        db.AuditRecords.Add(record);
    }

    private static string ScopeKey(GovernanceScopeKind scopeKind, Guid? merchantId) => scopeKind switch
    {
        GovernanceScopeKind.Platform => "platform",
        GovernanceScopeKind.Merchant when merchantId.HasValue => $"merchant:{merchantId.Value:D}",
        _ => throw new ArgumentException("Invalid governance scope."),
    };
}
