using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>Unit of work over the Scoped <see cref="ProducerDbContext"/>. Uses the provider's
/// execution strategy so the transaction is retry-safe under transient SQL Server faults.</summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly ProducerDbContext _db;

    public EfUnitOfWork(ProducerDbContext db) => _db = db;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Translate the provider-specific concurrency failure into an application-layer signal so
            // handlers can react without referencing EF Core.
            throw new ConcurrencyConflictException(
                "A concurrent change to the same record was detected; the save was rejected.", ex);
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await operation(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }
}
