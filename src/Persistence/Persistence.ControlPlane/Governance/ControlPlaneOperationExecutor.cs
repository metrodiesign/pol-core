using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using Governance.Domain;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Governance;

internal sealed class ControlPlaneOperationExecutor(
    ControlPlaneDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork,
    GovernanceSqlLockManager locks)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<(T Value, bool Replayed)> ExecuteAsync<T>(
        Guid actorId,
        Guid merchantId,
        string operation,
        string idempotencyKey,
        object intent,
        int successStatus,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) => ExecuteScopedAsync(
            actorId, GovernanceScopeKind.Merchant, merchantId, operation, idempotencyKey,
            intent, successStatus, null, action, cancellationToken);

    public Task<(T Value, bool Replayed)> ExecutePlatformAsync<T>(
        Guid actorId,
        string operation,
        string idempotencyKey,
        object intent,
        int successStatus,
        Func<CancellationToken, Task> acquireAuthorizationLock,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) => ExecuteScopedAsync(
            actorId, GovernanceScopeKind.Platform, null, operation, idempotencyKey,
            intent, successStatus, acquireAuthorizationLock, action, cancellationToken);

    private Task<(T Value, bool Replayed)> ExecuteScopedAsync<T>(
        Guid actorId,
        GovernanceScopeKind scopeKind,
        Guid? merchantId,
        string operation,
        string idempotencyKey,
        object intent,
        int successStatus,
        Func<CancellationToken, Task>? acquireAuthorizationLock,
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            ValidateKey(idempotencyKey);
            var hash = Hash(intent);
            if (acquireAuthorizationLock is not null)
                await acquireAuthorizationLock(ct);
            await locks.AcquireAsync($"admin-operation:{actorId:D}:{operation}:{HashKey(idempotencyKey)}", ct);
            var prior = await db.OperationRecords.SingleOrDefaultAsync(x =>
                x.ActorId == actorId && x.Operation == operation && x.IdempotencyKey == idempotencyKey, ct);
            if (prior is not null)
            {
                if (!FixedEquals(prior.RequestHash, hash))
                    throw new ConflictException(
                        "Idempotency key was reused with a different intent.", "idempotency_key_reused");
                if (prior.Status != OperationStatus.Succeeded || prior.ResponseBody is null)
                    throw new ConflictException(
                        "The operation is still in progress or has an unknown outcome.", "operation_in_progress");
                return (JsonSerializer.Deserialize<T>(prior.ResponseBody, Json)
                    ?? throw new InvalidOperationException("Stored operation result is invalid."), true);
            }

            var record = OperationRecord.Create(actorId, operation, idempotencyKey, hash,
                scopeKind, merchantId, clock.UtcNow, clock.UtcNow.AddHours(24));
            db.OperationRecords.Add(record);
            var value = await action(ct);
            record.Complete(successStatus, JsonSerializer.Serialize(value, Json), succeeded: true, clock.UtcNow);
            await unitOfWork.SaveChangesAsync(ct);
            return (value, false);
        }, cancellationToken);

    private static string Hash(object value) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json))).ToLowerInvariant();

    private static string HashKey(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private static void ValidateKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
            throw new InvalidRequestException("Idempotency-Key is invalid.", "validation_failed");
    }
}
