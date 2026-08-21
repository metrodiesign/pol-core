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

    public Task EnsureAsync(Guid configuredTenantId, CancellationToken cancellationToken)
    {
        if (configuredTenantId == Guid.Empty)
            throw new InvalidOperationException("Admin Microsoft workforce tenant configuration is invalid.");

        return unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
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
}
