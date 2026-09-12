using Accounts.Domain;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.IdentityAccess;

/// <summary>In-transaction Account authorization lease. The concurrency token on
/// <see cref="Account.AuthorizationVersion"/> makes a revoke that commits during the business write fail at
/// the same SaveChanges boundary as the write.</summary>
internal sealed class AccountAuthorizationLease(
    ControlPlaneDbContext db,
    ISecurityTelemetry telemetry)
{
    public async Task VerifyAsync(
        Guid accountId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Account authorization lease requires an active transaction.");

        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == accountId, cancellationToken);
        if (account is null || account.Status != AccountStatus.Active
            || account.AuthorizationVersion != expectedVersion)
        {
            telemetry.Emit(new DenialEvent(
                DenialCategory.AdminRevalidationDenial,
                "account",
                accountId,
                null,
                nameof(Account),
                "AccountAuthorizationLease.Verify",
                "Account authorization lease was stale or inactive.",
                CorrelationId.Current,
                DateTime.UtcNow));
            throw new WriteGuardException("Account authorization lease is stale or inactive.");
        }

        db.Entry(account).Property(x => x.AuthorizationVersion).IsModified = true;
    }
}
