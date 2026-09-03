using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane.Governance;

namespace Persistence.ControlPlane.Admins;

internal sealed class WorkforceTenantBindingStore(
    ControlPlaneDbContext db,
    IUnitOfWork unitOfWork,
    GovernanceSqlLockManager locks) : IWorkforceTenantBindingStore
{
    private const string LockResource = "admin-workforce-tenant-binding";
    private const string IdentityLockResource = "admin-user-identity-mutation";

    public Task EnsureAsync(Guid configuredTenantId, CancellationToken cancellationToken)
    {
        if (configuredTenantId == Guid.Empty)
            throw InvalidConfiguration();

        return unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(IdentityLockResource, ct).ConfigureAwait(false);
            TenantIdentityMigrationStateRow? tenantState = null;
            if (db.Database.IsSqlServer())
            {
                await EnsureHistoricalIdentityMigrationAsync(ct).ConfigureAwait(false);
                tenantState = await LoadTenantIdentityMigrationAsync(ct).ConfigureAwait(false);
            }

            await locks.AcquireAsync(LockResource, ct).ConfigureAwait(false);
            var existing = await db.WorkforceTenantBindings.SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (existing is null)
            {
                db.WorkforceTenantBindings.Add(WorkforceTenantBinding.Create(configuredTenantId));
                await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            else if (existing.TenantId != configuredTenantId)
            {
                throw new InvalidOperationException(
                    "Admin Microsoft Authority does not match the persisted workforce tenant binding.");
            }

            await EnsureFinalUsersAsync(configuredTenantId, tenantState, ct).ConfigureAwait(false);
            return 0;
        }, cancellationToken);
    }

    public async Task<Guid> GetRequiredTenantIdAsync(CancellationToken cancellationToken)
    {
        var bindings = await db.WorkforceTenantBindings.AsNoTracking().Take(2)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return bindings is [{ TenantId: var tenantId }] && tenantId != Guid.Empty
            ? tenantId
            : throw new InvalidOperationException("Admin Microsoft workforce tenant binding is unavailable.");
    }

    private async Task EnsureHistoricalIdentityMigrationAsync(CancellationToken cancellationToken)
    {
        var states = await db.Database.SqlQueryRaw<HistoricalIdentityMigrationStateRow>(
            """
            SELECT Id, CompletedAt, SnapshotCount, ConvertedCount, NoOpCount
            FROM admin.WorkforceIdentityMigrations;
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (states is not [{ Id: 1, CompletedAt: not null } state]
            || state.SnapshotCount < 0
            || state.ConvertedCount < 0
            || state.NoOpCount < 0
            || state.ConvertedCount + state.NoOpCount != state.SnapshotCount)
        {
            throw new InvalidOperationException("Admin Microsoft historical identity migration is incomplete.");
        }
    }

    private async Task<TenantIdentityMigrationStateRow> LoadTenantIdentityMigrationAsync(
        CancellationToken cancellationToken)
    {
        var states = await db.Database.SqlQueryRaw<TenantIdentityMigrationStateRow>(
            """
            SELECT Id, CompletedAt, SnapshotCount, MappedCount, NoOpCount
            FROM admin.WorkforceTenantIdentityMigrations;
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (states is not [{ Id: 1, CompletedAt: not null } state]
            || state.SnapshotCount < 0
            || state.MappedCount < 0
            || state.NoOpCount < 0
            || state.MappedCount + state.NoOpCount != state.SnapshotCount)
        {
            throw new InvalidOperationException("Admin Microsoft tenant identity migration is incomplete.");
        }

        return state;
    }

    private async Task EnsureFinalUsersAsync(
        Guid tenantId,
        TenantIdentityMigrationStateRow? state,
        CancellationToken cancellationToken)
    {
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = users.ToDictionary(user => user.Id);
        foreach (var user in users)
        {
            if (!MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
                    user.Provider, user.TenantId, user.Subject, tenantId, out _))
            {
                throw new InvalidOperationException("Admin Microsoft persisted identity state is invalid.");
            }
        }

        if (state is null)
            return;

        var snapshot = await db.Database.SqlQueryRaw<TenantIdentitySnapshotRow>(
            """
            SELECT AdminUserId
            FROM admin.WorkforceTenantIdentitySnapshot;
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (snapshot.Count != state.SnapshotCount
            || snapshot.Any(row =>
                !byId.TryGetValue(row.AdminUserId, out var user)
                || !MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
                    user.Provider, user.TenantId, user.Subject, tenantId, out var identityState)
                || identityState != MicrosoftWorkforceIdentityState.BoundMicrosoft))
        {
            throw new InvalidOperationException("Admin Microsoft tenant identity snapshot is invalid.");
        }
    }

    private static InvalidOperationException InvalidConfiguration() =>
        new("Admin Microsoft workforce tenant configuration is invalid.");
}

internal sealed class HistoricalIdentityMigrationStateRow
{
    public int Id { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SnapshotCount { get; set; }
    public int ConvertedCount { get; set; }
    public int NoOpCount { get; set; }
}

internal sealed class TenantIdentityMigrationStateRow
{
    public int Id { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SnapshotCount { get; set; }
    public int MappedCount { get; set; }
    public int NoOpCount { get; set; }
}

internal sealed class TenantIdentitySnapshotRow
{
    public Guid AdminUserId { get; set; }
}
