using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class PaymentAuthorizationLockIntegrationTests
{
    [Fact]
    public async Task Merchant_writer_holds_global_shared_lock_until_transaction_commits()
    {
        await using var merchantDb = NewContext();
        await using var cutoverDb = NewContext();
        await using var merchantTx = await merchantDb.Database.BeginTransactionAsync();
        await using var cutoverTx = await cutoverDb.Database.BeginTransactionAsync();
        var merchantLocks = new PaymentAuthorizationSqlLockManager(merchantDb);
        var cutoverLocks = new PaymentAuthorizationSqlLockManager(cutoverDb);

        await merchantLocks.AcquireMerchantExclusiveAsync(IntegrationDb.MerchantA, default);
        var cutoverAcquire = cutoverLocks.AcquireGlobalExclusiveAsync(default);
        await Task.Delay(200);
        Assert.False(cutoverAcquire.IsCompleted);

        await merchantTx.CommitAsync();
        await cutoverAcquire.WaitAsync(TimeSpan.FromSeconds(5));
        await cutoverTx.CommitAsync();
    }

    private static MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(IntegrationDb.SaConn, sql => sql.UseCompatibilityLevel(170)).Options,
        new Actor(), AllowAll.Instance, NoOpSecurityTelemetry.Instance);

    private sealed class Actor : IActorContext
    {
        public Guid MerchantId => IntegrationDb.MerchantA;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
