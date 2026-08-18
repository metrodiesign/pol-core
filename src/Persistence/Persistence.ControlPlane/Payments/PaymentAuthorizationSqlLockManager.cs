using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;

namespace Persistence.ControlPlane.Payments;

internal sealed class PaymentAuthorizationSqlLockManager(ControlPlaneDbContext db)
{
    public async Task AcquireGlobalExclusiveAsync(CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
            return;

        var results = await db.Database.SqlQueryRaw<int>(
            """
            DECLARE @lock int;
            EXEC @lock = sp_getapplock @Resource = N'payment-authz:global', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @lock AS Value;
            """).ToListAsync(cancellationToken);
        var result = results.Single();
        if (result < 0)
            throw new PaymentAuthorizationBusyException("Payment authorization state is busy.");
    }
}
