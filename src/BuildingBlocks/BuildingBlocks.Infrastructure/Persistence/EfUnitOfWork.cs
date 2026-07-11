using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace BuildingBlocks.Infrastructure.Persistence;

/// <summary>Unit of work over the Scoped <see cref="PolDbContext"/>. Uses the provider's
/// execution strategy so the transaction is retry-safe under transient SQL Server faults.</summary>
public sealed class EfUnitOfWork : IUnitOfWork
{
    private readonly PolDbContext _db;

    public EfUnitOfWork(PolDbContext db) => _db = db;

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
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A unique-index violation that races past an application-level pre-check (e.g. two admins
            // provisioning the same tenant code at once) is a 409, not a 500. Same layering rationale as
            // above — the application sees a domain conflict, never an EF/SQL type.
            throw new ConflictException(
                "A record with the same unique key already exists; the insert was rejected.", ex);
        }
    }

    // SQL Server: 2627 = unique constraint, 2601 = duplicate key in a unique index.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 or 2601 };

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
