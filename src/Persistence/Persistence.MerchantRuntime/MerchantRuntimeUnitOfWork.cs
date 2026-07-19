using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Persistence.MerchantRuntime;

/// <summary>Unit of work over the Scoped <see cref="MerchantRuntimeDbContext"/>. Uses the provider's
/// execution strategy so the transaction is retry-safe under transient SQL Server faults.</summary>
internal sealed class MerchantRuntimeUnitOfWork : IUnitOfWork
{
    private readonly MerchantRuntimeDbContext _db;
    private readonly ISecurityTelemetry _telemetry;

    public MerchantRuntimeUnitOfWork(MerchantRuntimeDbContext db, ISecurityTelemetry telemetry)
    {
        _db = db;
        _telemetry = telemetry;
    }

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
            Emit(DenialCategory.ConcurrencyConflict, "A stale/forged concurrency token was rejected at commit.");
            throw new ConcurrencyConflictException(
                "A concurrent change to the same record was detected; the save was rejected.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A unique-index violation that races past an application-level pre-check (e.g. two admins
            // provisioning the same merchant code at once) is a 409, not a 500. Same layering rationale as
            // above — the application sees a domain conflict, never an EF/SQL type.
            Emit(DenialCategory.CheckOrForeignKeyViolation, "Unique-key violation (SQL 2627/2601) at commit.");
            throw new ConflictException(
                "A record with the same unique key already exists; the insert was rejected.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 547 })
        {
            // SQL Server 547 = CHECK/FK constraint violation — the write floor's app-layer checks should have
            // caught this first; reaching the DB means a floor gap, so this is worth its own category (REQ-13.1).
            Emit(DenialCategory.CheckOrForeignKeyViolation, "CHECK/FK constraint violation (SQL 547) at commit.");
            throw new ConflictException(
                "The record violates a database constraint; the save was rejected.", ex);
        }
    }

    // SQL Server: 2627 = unique constraint, 2601 = duplicate key in a unique index.
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 or 2601 };

    private void Emit(DenialCategory category, string reason) =>
        _telemetry.Emit(new DenialEvent(
            category, "system", ActorId: null, TargetMerchant: null, nameof(MerchantRuntimeDbContext), "Save", reason,
            CorrelationId.Current, DateTime.UtcNow));

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
