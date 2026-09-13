using Microsoft.EntityFrameworkCore;

namespace Persistence.ControlPlane.Governance;

/// <summary>
/// Narrow SQL Server lock port for Governance writes. Every lock is transaction-owned; callers still
/// constrain entity reads and writes through <see cref="ControlPlaneDbContext"/>.
/// </summary>
internal sealed class GovernanceSqlLockManager(ControlPlaneDbContext db)
{
    public async Task AcquireAsync(string resource, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
            return;

        var results = await db.Database.SqlQueryRaw<int>(
            """
            DECLARE @lock int;
            EXEC @lock = sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @lock AS Value;
            """, resource).ToListAsync(cancellationToken);
        if (results.Single() < 0)
            throw new InvalidOperationException("Could not acquire the governance transaction lock.");
    }

    public async Task AcquireAuditHeadAsync(string scopeKey, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
            return;

        await db.Database.SqlQueryRaw<string>(
            "SELECT [ScopeKey] AS [Value] FROM admin.AuditHeads WITH (UPDLOCK,HOLDLOCK) WHERE [ScopeKey] = {0}",
            scopeKey).ToListAsync(cancellationToken);
    }
}
