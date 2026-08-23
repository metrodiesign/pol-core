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
            throw new InvalidOperationException("Admin Microsoft workforce tenant configuration is invalid.");

        return unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            await locks.AcquireAsync(IdentityLockResource, ct).ConfigureAwait(false);
            await EnsureIdentityMigrationAsync(ct).ConfigureAwait(false);
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

            return 0;
        }, cancellationToken);
    }

    private async Task EnsureIdentityMigrationAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
            return;

        var states = await db.Database.SqlQueryRaw<WorkforceIdentityMigrationStateRow>(
            """
            SELECT Id, CompletedAt, SnapshotCount, ConvertedCount, NoOpCount
            FROM admin.WorkforceIdentityMigrations;
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (states is not [{ Id: 1, CompletedAt: not null } state]
            || state.SnapshotCount < 0
            || state.ConvertedCount < 0
            || state.NoOpCount < 0
            || state.ConvertedCount + state.NoOpCount != state.SnapshotCount)
            throw new InvalidOperationException("Admin Microsoft workforce identity migration is incomplete.");

        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var expectedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            var expected = WorkforceEmail.TryCanonicalize(user.Email, out var canonical) ? canonical : null;
            if (expected is not null && !expectedKeys.Add(expected))
                throw new InvalidOperationException("Admin Microsoft workforce email ownership is invalid.");
            if (!string.Equals(user.WorkforceEmailKey, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("Admin Microsoft workforce email key is invalid.");

            if (user.Subject is not null
                && string.Equals(user.Provider, User.MicrosoftProvider, StringComparison.OrdinalIgnoreCase)
                && (!string.Equals(user.Provider, User.MicrosoftProvider, StringComparison.Ordinal)
                    || expected is null
                    || !string.Equals(user.Subject, expected, StringComparison.Ordinal)))
                throw new InvalidOperationException("Admin Microsoft workforce identity is invalid.");
        }
    }
}

internal sealed class WorkforceIdentityMigrationStateRow
{
    public int Id { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SnapshotCount { get; set; }
    public int ConvertedCount { get; set; }
    public int NoOpCount { get; set; }
}
