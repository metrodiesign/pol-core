using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;

namespace Persistence.MerchantRuntime.Payments;

internal sealed class PaymentAuthorizationSqlLockManager(MerchantRuntimeDbContext db)
    : IPaymentAuthorizationLockManager
{
    public Task AcquireGlobalExclusiveAsync(CancellationToken cancellationToken) =>
        AcquireAsync("payment-authz:global", "Exclusive", cancellationToken);

    public async Task AcquireMerchantSharedAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        RequireMerchant(merchantId);
        await AcquireAsync("payment-authz:global", "Shared", cancellationToken);
        await AcquireAsync($"payment-authz:merchant:{merchantId:D}", "Shared", cancellationToken);
    }

    public async Task AcquireMerchantExclusiveAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        RequireMerchant(merchantId);
        await AcquireAsync("payment-authz:global", "Shared", cancellationToken);
        await AcquireAsync($"payment-authz:merchant:{merchantId:D}", "Exclusive", cancellationToken);
    }

    private async Task AcquireAsync(string resource, string mode, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
            return;

        var sql = mode == "Shared"
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
        var results = await db.Database.SqlQueryRaw<int>(sql, resource).ToListAsync(cancellationToken);
        var result = results.Single();
        if (result < 0)
            throw new PaymentAuthorizationBusyException("Payment authorization state is busy.");
    }

    private static void RequireMerchant(Guid merchantId)
    {
        if (merchantId == Guid.Empty)
            throw new ArgumentException("MerchantId is required.", nameof(merchantId));
    }
}
