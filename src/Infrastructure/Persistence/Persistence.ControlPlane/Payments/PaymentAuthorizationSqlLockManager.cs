using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;

namespace Persistence.ControlPlane.Payments;

internal sealed class PaymentAuthorizationSqlLockManager(ControlPlaneDbContext db)
    : IPaymentAuthorizationLockManager
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

    public Task AcquireMerchantSharedAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        RequireMerchant(merchantId);
        return AcquireMerchantAsync(merchantId, "Shared", cancellationToken);
    }

    public Task AcquireMerchantExclusiveAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        RequireMerchant(merchantId);
        return AcquireMerchantAsync(merchantId, "Exclusive", cancellationToken);
    }

    private async Task AcquireMerchantAsync(Guid merchantId, string mode, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
            return;

        var sql = mode == "Shared"
            ? """
              DECLARE @lock int;
              EXEC @lock = sp_getapplock @Resource = N'payment-authz:global', @LockMode = N'Shared', @LockOwner = N'Transaction', @LockTimeout = 15000;
              SELECT @lock AS Value;
              """
            : """
              DECLARE @lock int;
              EXEC @lock = sp_getapplock @Resource = N'payment-authz:global', @LockMode = N'Shared', @LockOwner = N'Transaction', @LockTimeout = 15000;
              SELECT @lock AS Value;
              """;
        var global = await db.Database.SqlQueryRaw<int>(sql).ToListAsync(cancellationToken);
        if (global.Single() < 0)
            throw new PaymentAuthorizationBusyException("Payment authorization state is busy.");

        var merchantSql = mode == "Shared"
            ? """
              DECLARE @lock int;
              EXEC @lock = sp_getapplock @Resource = {0}, @LockMode = N'Shared', @LockOwner = N'Transaction', @LockTimeout = 15000;
              SELECT @lock AS Value;
              """
            : """
              DECLARE @lock int;
              EXEC @lock = sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
              SELECT @lock AS Value;
              """;
        var result = await db.Database.SqlQueryRaw<int>(merchantSql, $"payment-authz:merchant:{merchantId:D}")
            .ToListAsync(cancellationToken);
        if (result.Single() < 0)
            throw new PaymentAuthorizationBusyException("Payment authorization state is busy.");
    }

    private static void RequireMerchant(Guid merchantId)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
    }
}
