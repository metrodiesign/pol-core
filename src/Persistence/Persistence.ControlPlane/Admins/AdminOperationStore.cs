using System.Security.Cryptography;
using System.Text;
using Admins.Application.Users;
using Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane.Governance;

namespace Persistence.ControlPlane.Admins;

internal sealed class AdminOperationStore(
    ControlPlaneDbContext db,
    GovernanceSqlLockManager locks) : IAdminOperationStore
{
    public Task AcquireAsync(
        Guid actorId, string operation, string idempotencyKey, CancellationToken cancellationToken)
    {
        // ponytail: serialize this rare operation per actor. This deliberately matches every database-collation
        // equivalent idempotency key without reproducing SQL collation rules in application code.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{actorId:D}\n{operation}"));
        return locks.AcquireAsync($"admin-operation:{Convert.ToHexString(bytes)}", cancellationToken);
    }

    public async Task<AdminOperationReplay?> FindAsync(
        Guid actorId, string operation, string idempotencyKey, CancellationToken cancellationToken)
    {
        var record = await db.OperationRecords.SingleOrDefaultAsync(x =>
            x.ActorId == actorId && x.Operation == operation && x.IdempotencyKey == idempotencyKey,
            cancellationToken);
        return record is null
            ? null
            : new AdminOperationReplay(
                record.RequestHash, record.ResponseBody, record.Status != OperationStatus.Succeeded);
    }

    public void AddSucceeded(
        Guid actorId, string operation, string idempotencyKey, string requestHash,
        int responseStatus, string responseBody, DateTime now, DateTime expiresAt)
    {
        var record = OperationRecord.Create(
            actorId, operation, idempotencyKey, requestHash,
            GovernanceScopeKind.Platform, merchantId: null, now, expiresAt);
        record.Complete(responseStatus, responseBody, succeeded: true, now);
        db.OperationRecords.Add(record);
    }
}
