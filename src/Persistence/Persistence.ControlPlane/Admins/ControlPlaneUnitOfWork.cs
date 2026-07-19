using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Admins;

/// <summary>
/// Unit of work over <see cref="ControlPlaneDbContext"/>, keyed "admin" (task 8.5.1). Mirrors
/// <c>Api.Admins.ProvisioningUnitOfWork</c> exactly — that is the class the keyed "admin" <c>IUnitOfWork</c>
/// registration in <c>ScopedServices.cs</c> currently points at, i.e. the ACTUAL production behavior for the
/// admin side today (not the pol_app <c>EfUnitOfWork</c> shape). It clears the change tracker at the START of
/// every transaction attempt: provisioning stages new entities (each with a fresh Guid and a UNIQUE merchant
/// code), so a retried attempt that did not clear would re-insert the previous attempt's rows and hit a
/// duplicate-key violation. Clearing makes each attempt independent (REQ-4.1).
/// </summary>
internal sealed class ControlPlaneUnitOfWork : IUnitOfWork
{
    private readonly ControlPlaneDbContext _db;
    private readonly ISecurityTelemetry _telemetry;

    public ControlPlaneUnitOfWork(ControlPlaneDbContext db, ISecurityTelemetry telemetry)
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
            Emit(DenialCategory.ConcurrencyConflict, "A stale/forged concurrency token was rejected at commit.");
            throw new ConcurrencyConflictException(
                "A concurrent change to the same record was detected; the save was rejected.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2627 or 2601 })
        {
            // SQL Server 2627/2601 = unique-violation. A duplicate merchant code that races past the
            // ExistsByCodeAsync pre-check lands here -> surface a domain conflict (409), not an opaque 500.
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

    private void Emit(DenialCategory category, string reason) =>
        _telemetry.Emit(new DenialEvent(
            category, "system", ActorId: null, TargetMerchant: null, nameof(ControlPlaneDbContext), "Save", reason,
            CorrelationId.Current, DateTime.UtcNow));

    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear(); // each retry attempt starts from a clean slate (see class summary)
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await operation(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);
    }
}
