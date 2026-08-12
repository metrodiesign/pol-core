namespace BuildingBlocks.Application;

public sealed record AdminOperationRequest(
    Guid MerchantId,
    Guid ActorId,
    string Operation,
    string IdempotencyKey,
    string Intent,
    int SuccessStatus);

public sealed record AdminOperationResult<T>(T Value, bool Replayed);

/// <summary>Runs one Admin mutation with its MerchantRuntime-owned replay record in one transaction.</summary>
public interface IAdminOperationExecutor
{
    Task<AdminOperationResult<T>> ExecuteAsync<T>(
        AdminOperationRequest request,
        Func<CancellationToken, Task<T>> operation,
        Func<T, string?> resourceId,
        CancellationToken cancellationToken);

    Task<AdminOperationResult<T>> ExecuteRecoverableAsync<T>(
        AdminOperationRequest request,
        Func<CancellationToken, Task<T>> operation,
        Func<T, string?> resourceId,
        CancellationToken cancellationToken);
}
