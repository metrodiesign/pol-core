using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime.Idempotency;

/// <summary>
/// Commerce-owned replay executor. The operation record and the business mutation share the
/// <see cref="CommerceDbContext"/> transaction, so an order/cart/payment/notification mutation never
/// claims idempotency in the Control Plane database.
/// </summary>
internal sealed class AdminOperationExecutor(
    CommerceDbContext db,
    IClock clock,
    IUnitOfWork unitOfWork) : IAdminOperationExecutor
{
    private static readonly JsonSerializerOptions Json = new(OutboxSerializer.Options);

    public Task<AdminOperationResult<T>> ExecuteAsync<T>(
        AdminOperationRequest request,
        Func<CancellationToken, Task<T>> operation,
        Func<T, string?> resourceId,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var intentHash = Hash(request.Intent);
            var prior = await PlatformReadGuard.ReadAsync(token => db.AdminOperationRecords.IgnoreQueryFilters()
                .AsNoTracking().SingleOrDefaultAsync(x =>
                    x.MerchantId == request.MerchantId
                    && x.ActorId == request.ActorId
                    && x.Operation == request.Operation
                    && x.IdempotencyKey == request.IdempotencyKey, token), ct);
            if (prior is not null)
            {
                EnsureIntent(prior, intentHash);
                if (prior.State != AdminOperationState.Succeeded || prior.Result is null)
                    throw new ConflictException(
                        "The operation is still in progress or has an unknown outcome.", "operation_in_progress");
                return new AdminOperationResult<T>(
                    JsonSerializer.Deserialize<T>(prior.Result, Json)
                        ?? throw new InvalidOperationException("Stored operation result is invalid."),
                    true);
            }

            var record = AdminOperationRecord.Create(
                request.MerchantId, request.ActorId, request.Operation,
                request.IdempotencyKey, intentHash, clock.UtcNow);
            db.AdminOperationRecords.Add(record);
            await unitOfWork.SaveChangesAsync(ct);

            var value = await operation(ct);
            record.Succeed(request.SuccessStatus, JsonSerializer.Serialize(value, Json), resourceId(value));
            await unitOfWork.SaveChangesAsync(ct);
            return new AdminOperationResult<T>(value, false);
        }, cancellationToken);

    public async Task<AdminOperationResult<T>> ExecuteRecoverableAsync<T>(
        AdminOperationRequest request,
        Func<CancellationToken, Task<T>> operation,
        Func<T, string?> resourceId,
        CancellationToken cancellationToken)
    {
        var intentHash = Hash(request.Intent);
        var replay = await unitOfWork.ExecuteInTransactionAsync(
            async ct => await ClaimOrReplayAsync<T>(request, intentHash, ct), cancellationToken);
        if (replay is not null)
            return replay;

        // The Commerce operation owns its durable claim and PSP idempotency. A failed call leaves the
        // row InProgress so a same-intent retry resumes the target state machine.
        var value = await operation(cancellationToken);
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var record = await PlatformReadGuard.ReadAsync(token => db.AdminOperationRecords.IgnoreQueryFilters()
                .SingleAsync(x => x.MerchantId == request.MerchantId
                    && x.ActorId == request.ActorId
                    && x.Operation == request.Operation
                    && x.IdempotencyKey == request.IdempotencyKey, token), ct);
            if (record.State != AdminOperationState.Succeeded)
            {
                record.Succeed(request.SuccessStatus, JsonSerializer.Serialize(value, Json), resourceId(value));
                await unitOfWork.SaveChangesAsync(ct);
            }
            return true;
        }, cancellationToken);
        return new AdminOperationResult<T>(value, false);
    }

    private async Task<AdminOperationResult<T>?> ClaimOrReplayAsync<T>(
        AdminOperationRequest request, string intentHash, CancellationToken ct)
    {
        var prior = await PlatformReadGuard.ReadAsync(token => db.AdminOperationRecords.IgnoreQueryFilters()
            .AsNoTracking().SingleOrDefaultAsync(x => x.MerchantId == request.MerchantId
                && x.ActorId == request.ActorId
                && x.Operation == request.Operation
                && x.IdempotencyKey == request.IdempotencyKey, token), ct);
        if (prior is not null)
        {
            EnsureIntent(prior, intentHash);
            if (prior.State == AdminOperationState.Succeeded && prior.Result is not null)
                return new AdminOperationResult<T>(
                    JsonSerializer.Deserialize<T>(prior.Result, Json)
                        ?? throw new InvalidOperationException("Stored operation result is invalid."), true);
            return null;
        }

        db.AdminOperationRecords.Add(AdminOperationRecord.Create(
            request.MerchantId, request.ActorId, request.Operation,
            request.IdempotencyKey, intentHash, clock.UtcNow));
        await unitOfWork.SaveChangesAsync(ct);
        return null;
    }

    private static string Hash(string intent) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(intent)));

    private static void EnsureIntent(AdminOperationRecord record, string hash)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(record.IntentHash), Encoding.ASCII.GetBytes(hash)))
            throw new ConflictException(
                "Idempotency key was reused with a different intent.", "idempotency_key_reused");
    }
}
