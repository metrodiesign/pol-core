namespace BuildingBlocks.Application;

/// <summary>
/// The transactional seam application handlers use to commit without referencing a DbContext. The
/// payment/webhook path runs idempotency-claim + state transition + outbox-enqueue inside one
/// <see cref="ExecuteInTransactionAsync{T}"/> so they commit atomically (PLAN decisions #9, #10).
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken);
}
