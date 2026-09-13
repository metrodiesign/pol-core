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

    [Fact]
    public async Task Session_creation_shared_lock_waits_for_an_environment_activation_exclusive_lock()
    {
        // AC-4.8 / critical scenario #3: create-session takes the Merchant SHARED lock, while an environment
        // activation (task 7) takes the same Merchant lock EXCLUSIVELY. This drives the real applock against
        // SQL Server to prove the two serialize — while the exclusive holder is uncommitted, a concurrent
        // session-creation shared acquire cannot proceed, and only completes once the activation commits. The
        // exclusive side is acquired directly (no task-7 code is built here).
        await using var activationDb = NewContext();
        await using var sessionDb = NewContext();
        await using var activationTx = await activationDb.Database.BeginTransactionAsync();
        await using var sessionTx = await sessionDb.Database.BeginTransactionAsync();
        var activationLocks = new PaymentAuthorizationSqlLockManager(activationDb);
        var sessionLocks = new PaymentAuthorizationSqlLockManager(sessionDb);

        await activationLocks.AcquireMerchantExclusiveAsync(IntegrationDb.MerchantA, default);
        var sessionAcquire = sessionLocks.AcquireMerchantSharedAsync(IntegrationDb.MerchantA, default);
        await Task.Delay(200);
        Assert.False(sessionAcquire.IsCompleted); // the shared acquire is blocked by the exclusive holder

        await activationTx.CommitAsync();
        await sessionAcquire.WaitAsync(TimeSpan.FromSeconds(5)); // released once the activation commits
        await sessionTx.CommitAsync();
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
