using System.Data;
using BuildingBlocks.Application;
using Iam.Application.Roles;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Accounts.Domain;

namespace Persistence.ControlPlane.Iam;

/// <summary>Control Plane implementation of the IAM authorization invalidation port. Assigned Account rows are
/// collected, sorted, and locked with UPDLOCK/HOLDLOCK before the Role aggregate is mutated by its caller. The
/// lock order matches CommerceAuthorizationLease's Account-first order, so a concurrent commerce write either
/// commits before this revoke or is rejected after this version bump.</summary>
internal sealed class RoleAuthorizationInvalidator(
    ControlPlaneDbContext db,
    IClock clock)
    : IRoleAuthorizationInvalidator
{
    public async Task InvalidateAssignedAccountsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        if (roleId == Guid.Empty)
            throw new ArgumentException("Role id is required.", nameof(roleId));
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Role authorization invalidation requires an active transaction.");

        var merchantAccountIds = await (
                from assignment in db.AccessRoles.AsNoTracking()
                join access in db.AccountMerchantAccess.AsNoTracking()
                    on new { assignment.MerchantAccessId, assignment.MerchantId }
                    equals new { MerchantAccessId = access.Id, access.MerchantId }
                where assignment.RoleId == roleId
                select access.AccountId)
            .ToListAsync(cancellationToken);
        var employeeAccountIds = await (
                from assignment in db.PlatformAccessRoles.AsNoTracking()
                join access in db.PlatformAccess.AsNoTracking()
                    on assignment.PlatformAccessId equals access.Id
                where assignment.RoleId == roleId
                select access.EmployeeAccountId)
            .ToListAsync(cancellationToken);

        foreach (var accountId in merchantAccountIds.Concat(employeeAccountIds)
                     .Where(id => id != Guid.Empty)
                     .Distinct()
                     .OrderBy(id => id))
        {
            var locked = await db.Database.SqlQueryRaw<Guid>(
                    "SELECT [Id] AS [Value] FROM [acct].[Accounts] WITH (UPDLOCK,HOLDLOCK) WHERE [Id] = @accountId",
                    new SqlParameter("@accountId", SqlDbType.UniqueIdentifier) { Value = accountId })
                .ToListAsync(cancellationToken);
            if (locked.Count == 0)
                continue;

            var account = await db.Accounts.SingleAsync(x => x.Id == accountId, cancellationToken);
            account.BumpAuthorizationVersion(clock.UtcNow);
        }
    }
}
