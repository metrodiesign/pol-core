using BuildingBlocks.Infrastructure.Persistence;
using BuildingBlocks.Infrastructure.Persistence.MigrationReadiness;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Platform.Application.Migration;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "MigrationReadiness")]
[Collection("MigrationReadinessSql")]
public sealed class Task9MigrationReadinessSqlIntegrationTests
{
    private const string DatabaseName = "PolMigrationReadinessTask9Test";
    private static readonly DateTime CapturedAt = new(2026, 9, 10, 22, 30, 0, DateTimeKind.Utc);
    private static readonly Guid RunId = Guid.Parse("91000000-0000-0000-0000-000000000001");
    private static readonly Guid MerchantId = Guid.Parse("91000000-0000-0000-0000-000000000002");
    private static readonly Guid OrderId = Guid.Parse("91000000-0000-0000-0000-000000000003");
    private static readonly Guid BranchId = Guid.Parse("91000000-0000-0000-0000-000000000004");
    private static readonly Guid SaleId = Guid.Parse("91000000-0000-0000-0000-000000000005");
    private static readonly Guid ProviderAccountId = Guid.Parse("91000000-0000-0000-0000-000000000006");
    private static readonly Guid CredentialVersionId = Guid.Parse("91000000-0000-0000-0000-000000000007");
    private static readonly Guid TransactionId = Guid.Parse("91000000-0000-0000-0000-000000000010");
    private const string LegacyDatabaseName = "PolMigrationLegacyTask9Test";
    private const string RehearsalDatabaseName = "PolMigrationRehearsalTask9Test";
    private const string BackupPath = "/var/opt/mssql/backup/pol-task9-synthetic.bak";

    [Fact]
    [Trait("Requirement", "REQ-11.2")]
    [Trait("Requirement", "REQ-11.3")]
    [Trait("Requirement", "REQ-11.5")]
    [Trait("Requirement", "REQ-11.6")]
    [Trait("Requirement", "REQ-11.7")]
    [Trait("Requirement", "REQ-11.8")]
    [Trait("Requirement", "REQ-11.11")]
    [Trait("Requirement", "REQ-12.2")]
    [Trait("Requirement", "REQ-12.3")]
    public async Task Synthetic_backup_restore_rehearsal_backfills_real_target_owners_without_external_calls()
    {
        await DropDatabaseIfExistsAsync(LegacyDatabaseName);
        await DropDatabaseIfExistsAsync(RehearsalDatabaseName);
        try
        {
            await CreateDatabaseAsync(LegacyDatabaseName);
            await MigrateAsync(LegacyDatabaseName);
            await SeedLegacySnapshotAsync(LegacyDatabaseName);
            await BackupDatabaseAsync(LegacyDatabaseName);
            await RestoreDatabaseAsync(RehearsalDatabaseName);

            var input = await ReadLegacyInputAsync(RehearsalDatabaseName);
            var capture = new MigrationSideEffectCapture();
            var result = new MigrationReadinessRunner().Rehearse(input, CapturedAt, capture);

            Assert.Equal(MigrationRehearsalStatus.Passed, result.Status);
            Assert.True(result.Invariants.Passed);
            Assert.Equal(0, result.ExternalCallCount);
            Assert.Equal(0, capture.TotalCalls);

            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(RehearsalDatabaseName));
            await using var successorConnection = new SqlConnection(IntegrationDb.SaConnFor(RehearsalDatabaseName));
            var store = new SqlMigrationReadinessStore(connection);
            await store.PersistAsync(result, default);
            var ownerLease = await new SqlMigrationMaintenanceLease(connection)
                .AcquireAsync(RunId, "backup-owner", default);
            var contender = new SqlMigrationMaintenanceLease(successorConnection);
            await Assert.ThrowsAsync<MigrationMaintenanceException>(() =>
                contender.AcquireAsync(Guid.Parse("91000000-0000-0000-0000-000000000099"), "backup-contender", default));
            await connection.CloseAsync();
            await connection.OpenAsync();
            await Assert.ThrowsAsync<MigrationMaintenanceException>(() =>
                store.BackfillTargetsAsync(result, ownerLease, default));
            await ownerLease.DisposeAsync();
            await using var successorLease = await contender.AcquireAsync(RunId, "backup-successor", default);
            var successorStore = new SqlMigrationReadinessStore(successorConnection);
            var recoveryCoordinator = new MigrationMaintenanceCoordinator();
            using var recoveryWriter = recoveryCoordinator.AcquireWriter(RunId, "backup-recovery-writer");
            var recoveryPause = recoveryCoordinator.Pause(recoveryWriter);
            var recoveryEntry = recoveryCoordinator.ReceiveCallback(recoveryPause, input.PendingCallbacks.Single());
            await successorStore.BackfillTargetsAsync(result, successorLease, default);
            await successorStore.SaveRecoveryCallbackAsync(recoveryEntry, default);
            var replayedCallbacks = await successorStore.ReplayPendingAsync(
                RunId, recoveryPause.Watermark, CapturedAt.AddMinutes(1), default);
            Assert.Single(replayedCallbacks);

            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM shop.Orders WHERE Id = @order AND MerchantId = @merchant;",
                ("@order", OrderId), ("@merchant", MerchantId))));
            Assert.Equal("THB", Convert.ToString(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT AmountCurrency FROM shop.Orders WHERE Id = @order;", ("@order", OrderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM acct.Accounts WHERE Id = @account;",
                ("@account", result.IdentityMap.Single(x => x.LegacyId == "active-1").AccountId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM acct.LoginAccounts WHERE AccountId = @account;",
                ("@account", result.IdentityMap.Single(x => x.LegacyId == "active-1").AccountId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM acct.AgentRegistrations WHERE MerchantId = @merchant;",
                ("@merchant", MerchantId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM acct.AgentRegistrationAttempts WHERE MerchantId = @merchant;",
                ("@merchant", MerchantId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM txn.Transactions WHERE Id = @transaction AND OrderId = @order;",
                ("@transaction", TransactionId), ("@order", OrderId))));
            Assert.Equal("request-backup-1", Convert.ToString(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT ProviderRequestReference FROM txn.Transactions WHERE Id = @transaction;",
                ("@transaction", TransactionId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM txn.TransactionEvents WHERE TransactionId = @transaction;",
                ("@transaction", TransactionId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM cfg.LegacyIdentityMaps WHERE RunId = @run;", ("@run", RunId))));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM cfg.MigrationConflicts WHERE RunId = @run;", ("@run", RunId))));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId = @merchant;", ("@merchant", MerchantId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT Status FROM cfg.MigrationRecoveryInbox WHERE RunId = @run AND CallbackId = N'legacy-callback-1';",
                ("@run", RunId))));
            Assert.Contains("MIGRATION_BACKFILL", Convert.ToString(await IntegrationDb.ScalarAsync(successorConnection,
                "SELECT OrderSnapshot FROM txn.Transactions WHERE Id = @transaction;", ("@transaction", TransactionId)))!);
        }
        finally
        {
            await DropDatabaseIfExistsAsync(RehearsalDatabaseName);
            await DropDatabaseIfExistsAsync(LegacyDatabaseName);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-11.3")]
    [Trait("Requirement", "REQ-11.6")]
    [Trait("Requirement", "REQ-11.7")]
    [Trait("Requirement", "REQ-11.11")]
    [Trait("Requirement", "REQ-12.2")]
    public async Task Fresh_isolated_sql_rehearsal_persists_mapping_transaction_invariants_and_zero_calls()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var input = LegacyMigrationInput.Create(
                RunId,
                "synthetic-backup-task9-1",
                [new LegacyHuman(
                    new LegacyKey("Human", "active-1"),
                    LegacyHumanStatus.Active,
                    new IdentityEvidence("entra", "tenant", "subject-1", "evidence-1"),
                    MerchantId,
                    null,
                    "ignored@example.invalid",
                    "Ignored")],
                [new LegacyHumanSession(new LegacyKey("Human", "active-1"), "legacy-session-1", CapturedAt)],
                [new LegacyPaymentSession(
                    Guid.Parse("91000000-0000-0000-0000-000000000010"),
                    OrderId,
                    MerchantId,
                    "provider-account-1",
                    "SANDBOX",
                    "request-1",
                    "charge-1",
                    100m,
                    "THB",
                    "SUCCEEDED",
                    [new LegacyPaymentHistory("event-1", "SUCCEEDED", CapturedAt, "charge-1")],
                    "{\"legacy\":true}")]);
            var result = new MigrationReadinessRunner().Rehearse(input, CapturedAt);
            Assert.Equal(MigrationRehearsalStatus.Passed, result.Status);
            Assert.Equal(0, result.ExternalCallCount);
            Assert.True(result.Invariants.Passed);

            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(DatabaseName));
            var store = new SqlMigrationReadinessStore(connection);
            await store.PersistAsync(result, default);

            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.MigrationRuns WHERE RunId = @runId;", ("@runId", RunId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.LegacyIdentityMaps WHERE RunId = @runId AND LegacyKind = N'Human' AND LegacyId = N'active-1';",
                ("@runId", RunId))));
            Assert.Equal(OrderId, Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(connection,
                "SELECT OrderId FROM cfg.MigratedTransactions WHERE RunId = @runId;", ("@runId", RunId)))!));
            Assert.Equal("request-1", Convert.ToString(await IntegrationDb.ScalarAsync(connection,
                "SELECT PspRequestReference FROM cfg.MigratedTransactions WHERE RunId = @runId;", ("@runId", RunId))));
            Assert.Equal("MIGRATION_BACKFILL", Convert.ToString(await IntegrationDb.ScalarAsync(connection,
                "SELECT Provenance FROM cfg.MigratedTransactions WHERE RunId = @runId;", ("@runId", RunId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-11.4")]
    [Trait("Requirement", "REQ-12.3")]
    public async Task Conflict_report_is_durable_and_keeps_cutover_blocked()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var result = new MigrationReadinessRunner().Rehearse(
                LegacyMigrationInput.Create(
                    RunId,
                    "synthetic-backup-task9-conflict",
                    [new LegacyHuman(new LegacyKey("Human", "missing-evidence"), LegacyHumanStatus.Active,
                        null, MerchantId, null, "secret@example.invalid", "Do not use")],
                    [],
                    []),
                CapturedAt);
            Assert.True(result.CutoverBlocked);
            Assert.Equal(0, result.ExternalCallCount);

            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(DatabaseName));
            await new SqlMigrationReadinessStore(connection).PersistAsync(result, default);
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.MigrationConflicts WHERE RunId = @runId AND ResolutionStatus = N'UNRESOLVED';",
                ("@runId", RunId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT Status FROM cfg.MigrationRuns WHERE RunId = @runId;", ("@runId", RunId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-11.8")]
    [Trait("Requirement", "REQ-11.9")]
    public async Task Recovery_inbox_replays_callbacks_after_watermark_and_preserves_post_cutover_target()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var result = new MigrationReadinessRunner().Rehearse(
                LegacyMigrationInput.Create(RunId, "synthetic-backup-task9-recovery", [], [], []), CapturedAt);
            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(DatabaseName));
            var store = new SqlMigrationReadinessStore(connection);
            await store.PersistAsync(result, default);

            var coordinator = new MigrationMaintenanceCoordinator();
            using var writer = coordinator.AcquireWriter(RunId, "task9-sql-writer");
            var pause = coordinator.Pause(writer);
            var entry = coordinator.ReceiveCallback(
                pause,
                new LegacyCallback("callback-sql-1", "charge-sql-1", CapturedAt, "{\"ok\":true}", 7));
            await store.SaveRecoveryCallbackAsync(entry, default);

            var replayed = await store.ReplayPendingAsync(RunId, pause.Watermark, CapturedAt.AddMinutes(1), default);
            Assert.Single(replayed);
            Assert.Equal("callback-sql-1", replayed[0].CallbackId);
            Assert.Empty(await store.ReplayPendingAsync(RunId, pause.Watermark, CapturedAt.AddMinutes(2), default));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT Status FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId AND CallbackId = N'callback-sql-1';",
                ("@runId", RunId))));

            var rollback = coordinator.RollbackForward(
                pause,
                new ForwardRollbackInput(
                    new Dictionary<Guid, string> { [OrderId] = "PAID" },
                    ["post-cutover-success", "post-cutover-event"],
                    pause.Watermark),
                CapturedAt.AddMinutes(3));
            Assert.True(rollback.TargetPreserved);
            Assert.False(rollback.BackupRestored);
            Assert.Equal("PAID", rollback.PreservedResults[OrderId]);
            Assert.Equal(["post-cutover-success", "post-cutover-event"], rollback.PreservedEvents);
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-11.7")]
    public async Task Backfill_maps_failed_transaction_to_unpaid_not_processing()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            // Seed the target order at Processing so the backfill CASE arm is observable.
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                await IntegrationDb.ExecAsync(seed, """
                    INSERT shop.Orders
                        (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                         IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                         CustomerName, CustomerPhone, CustomerEmail, AmountAmount, AmountCurrency,
                         SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                         OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
                    VALUES (@order, @merchant, N'ORD6900000777', 8, 2, @at, @at, 2, 1, @at, @at,
                            NULL, NULL, N'Failed buyer', '0800000000', 'buyer@example.invalid', 100.00, 'THB',
                            100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
                    """,
                    ("@order", OrderId), ("@merchant", MerchantId), ("@at", CapturedAt));
            }

            var input = LegacyMigrationInput.Create(
                RunId, "synthetic-backup-task9-failed", [], [],
                [new LegacyPaymentSession(
                    TransactionId, OrderId, MerchantId, "provider-account-failed", "SANDBOX",
                    "request-failed", "charge-failed", 100m, "THB", "FAILED",
                    [new LegacyPaymentHistory("evt-failed", "FAILED", CapturedAt, null)],
                    "{\"legacy\":true}",
                    ProviderAccountId: ProviderAccountId, CredentialVersionId: CredentialVersionId)]);
            var result = new MigrationReadinessRunner().Rehearse(input, CapturedAt);
            Assert.Equal(MigrationRehearsalStatus.Passed, result.Status);

            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(DatabaseName));
            var store = new SqlMigrationReadinessStore(connection);
            await store.PersistAsync(result, default);
            await using var lease = await new SqlMigrationMaintenanceLease(connection)
                .AcquireAsync(RunId, "task9-failed-backfill", default);
            await store.BackfillTargetsAsync(result, lease, default);

            // FAILED (TransactionStatus 4) must map the order to Unpaid (1), not fall through to Processing (2).
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT PaymentStatus FROM shop.Orders WHERE Id = @order AND MerchantId = @merchant;",
                ("@order", OrderId), ("@merchant", MerchantId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-11.8")]
    public async Task Recovery_inbox_replay_stamps_only_the_rows_it_read()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var result = new MigrationReadinessRunner().Rehearse(
                LegacyMigrationInput.Create(RunId, "synthetic-backup-task9-multi", [], [], []), CapturedAt);
            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(DatabaseName));
            var store = new SqlMigrationReadinessStore(connection);
            await store.PersistAsync(result, default);

            var coordinator = new MigrationMaintenanceCoordinator();
            using var writer = coordinator.AcquireWriter(RunId, "task9-multi-writer");
            var pause = coordinator.Pause(writer);
            // Two pending callbacks past the watermark exercise the dynamic Sequence IN (...) update.
            await store.SaveRecoveryCallbackAsync(
                coordinator.ReceiveCallback(pause,
                    new LegacyCallback("callback-multi-1", "charge-multi-1", CapturedAt, "{\"ok\":1}", 11)), default);
            await store.SaveRecoveryCallbackAsync(
                coordinator.ReceiveCallback(pause,
                    new LegacyCallback("callback-multi-2", "charge-multi-2", CapturedAt, "{\"ok\":2}", 12)), default);

            var replayed = await store.ReplayPendingAsync(RunId, pause.Watermark, CapturedAt.AddMinutes(1), default);
            Assert.Equal(2, replayed.Count);
            Assert.Equal(["callback-multi-1", "callback-multi-2"], replayed.Select(x => x.CallbackId).OrderBy(x => x));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId AND Status = 2;",
                ("@runId", RunId))));
            Assert.Empty(await store.ReplayPendingAsync(RunId, pause.Watermark, CapturedAt.AddMinutes(2), default));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-11.8")]
    public async Task Recovery_inbox_surfaces_pk_collision_when_a_different_callback_reuses_a_sequence()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var result = new MigrationReadinessRunner().Rehearse(
                LegacyMigrationInput.Create(RunId, "synthetic-backup-task9-pk", [], [], []), CapturedAt);
            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(DatabaseName));
            var store = new SqlMigrationReadinessStore(connection);
            await store.PersistAsync(result, default);

            // Pin the constraint name the catch filter matches on: a rename would turn the swallow
            // path into dead code (concurrency dedupe would start throwing) while this test still
            // exercised only the rethrow path below.
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID(N'cfg.MigrationRecoveryInbox') AND name = N'UQ_MigrationRecoveryInbox_Run_Callback';")));

            // The MigrationMaintenanceCoordinator._sequence counter is in-memory and resets to 0 on
            // process restart, so a DIFFERENT callback can reuse an already-persisted (RunId, Sequence).
            var first = new MigrationRecoveryInboxEntry(
                RunId, 5, "callback-pk-a", "charge-pk-a", CapturedAt, "{\"ok\":\"a\"}",
                MigrationRecoveryStatus.Pending, null);
            var collision = new MigrationRecoveryInboxEntry(
                RunId, 5, "callback-pk-b", "charge-pk-b", CapturedAt, "{\"ok\":\"b\"}",
                MigrationRecoveryStatus.Pending, null);

            await store.SaveRecoveryCallbackAsync(first, default);

            // The PK_MigrationRecoveryInbox (RunId, Sequence) violation must surface loudly, not be
            // swallowed as if it were the UQ (RunId, CallbackId) dedupe backstop.
            var thrown = await Assert.ThrowsAsync<SqlException>(
                () => store.SaveRecoveryCallbackAsync(collision, default));
            Assert.Contains("PK_MigrationRecoveryInbox", thrown.Message, StringComparison.Ordinal);

            // Finding A is silent data loss: the losing callback "b" is gone and only "a" survives.
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId;", ("@runId", RunId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId AND CallbackId = N'callback-pk-a';",
                ("@runId", RunId))));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId AND CallbackId = N'callback-pk-b';",
                ("@runId", RunId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-11.8")]
    public async Task Recovery_inbox_dedupes_a_duplicate_callback_id_without_error()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAsync();
            var result = new MigrationReadinessRunner().Rehearse(
                LegacyMigrationInput.Create(RunId, "synthetic-backup-task9-dupe", [], [], []), CapturedAt);
            await using var connection = new SqlConnection(IntegrationDb.SaConnFor(DatabaseName));
            var store = new SqlMigrationReadinessStore(connection);
            await store.PersistAsync(result, default);

            // Same CallbackId at a different Sequence: the NOT EXISTS (RunId, CallbackId) probe dedupes
            // it, proving the dedupe key is CallbackId (not Sequence) and stays idempotent after the
            // catch filter was narrowed. The UQ backstop itself only fires under a genuine race
            // between probe and insert, which has no deterministic single-connection route.
            var original = new MigrationRecoveryInboxEntry(
                RunId, 3, "callback-dupe", "charge-dupe", CapturedAt, "{\"ok\":1}",
                MigrationRecoveryStatus.Pending, null);
            var duplicate = new MigrationRecoveryInboxEntry(
                RunId, 4, "callback-dupe", "charge-dupe", CapturedAt, "{\"ok\":2}",
                MigrationRecoveryStatus.Pending, null);

            await store.SaveRecoveryCallbackAsync(original, default);
            await store.SaveRecoveryCallbackAsync(duplicate, default);

            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId AND CallbackId = N'callback-dupe';",
                ("@runId", RunId))));
            // The winning row is the first one written; Sequence 4 was never persisted.
            Assert.Equal(3, Convert.ToInt64(await IntegrationDb.ScalarAsync(connection,
                "SELECT Sequence FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId AND CallbackId = N'callback-dupe';",
                ("@runId", RunId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    private static PolDbContext CreateMigrationContext(string database = DatabaseName) => new(
        new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
        new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration),
            typeof(Carts.Infrastructure.CartModuleRegistration),
            typeof(Orders.Infrastructure.OrdersModuleRegistration),
            typeof(Payments.Infrastructure.PaymentsModuleRegistration),
            typeof(Merchants.Infrastructure.MerchantsModuleRegistration),
            typeof(Admins.Infrastructure.AdminModuleRegistration),
            typeof(Iam.Infrastructure.IamModuleRegistration),
            typeof(Governance.Infrastructure.GovernanceModuleRegistration),
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration),
            typeof(Accounts.Infrastructure.AccountsModuleRegistration),
            typeof(Access.Infrastructure.AccessModuleRegistration),
        ]));

    private static async Task MigrateAsync(string database = DatabaseName)
    {
        await using var context = CreateMigrationContext(database);
        await context.Database.MigrateAsync();
    }

    private static async Task CreateDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master, $"CREATE DATABASE [{database}] COLLATE Thai_100_CI_AS;");
        await IntegrationDb.ExecAsync(master, $"ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = 170;");
        await using var target = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        await IntegrationDb.ExecAsync(target, "CREATE USER pol_app WITHOUT LOGIN;");
    }

    private static async Task SeedLegacySnapshotAsync(string database)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        await IntegrationDb.ExecAsync(connection, """
            INSERT merch.Merchants (Id, Code, Name, Note, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
            VALUES (@merchant, N'task9-synthetic', N'Task 9 Synthetic', NULL, 1, N'TH', N'THB', N'card', @at, N'{}');
            INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
            VALUES (@branch, @merchant, N'task9-branch', N'Task 9 Branch', 1, @at, @at, 1);
            INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
            VALUES (@sale, @merchant, @branch, N'task9-sale', N'Task 9 Sale', 1, @at, @at, 1);
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                 IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                 CustomerName, CustomerPhone, CustomerEmail, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
            VALUES (@order, @merchant, N'ORD6900000998', 8, 1, @at, @at, 2, 1, @at, @at,
                    NULL, NULL, N'Synthetic buyer', '0800000000', 'buyer@example.invalid', 100.00, 'THB',
                    100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
            CREATE TABLE dbo.LegacyHumans
            (
                LegacyId nvarchar(200) NOT NULL,
                Status int NOT NULL,
                Provider nvarchar(64) NULL,
                TenantId nvarchar(128) NULL,
                ExternalUserId nvarchar(256) NULL,
                EvidenceReference nvarchar(256) NULL,
                MerchantId uniqueidentifier NOT NULL,
                SaleId uniqueidentifier NULL,
                BranchId uniqueidentifier NULL,
                Email nvarchar(320) NULL,
                DisplayName nvarchar(200) NULL,
                PhoneNumber nvarchar(64) NULL,
                SaleCode nvarchar(64) NULL
            );
            CREATE TABLE dbo.LegacyPaymentSessions
            (
                Id uniqueidentifier NOT NULL,
                OrderId uniqueidentifier NOT NULL,
                MerchantId uniqueidentifier NOT NULL,
                ProviderAccountReference nvarchar(256) NOT NULL,
                Environment nvarchar(32) NOT NULL,
                PspRequestReference nvarchar(256) NOT NULL,
                PspTransactionReference nvarchar(256) NULL,
                Amount decimal(19,4) NOT NULL,
                Currency char(3) NOT NULL,
                Status nvarchar(64) NOT NULL,
                SnapshotJson nvarchar(max) NOT NULL,
                ProviderAccountId uniqueidentifier NOT NULL,
                CredentialVersionId uniqueidentifier NOT NULL,
                Provider int NOT NULL,
                ConfigurationVersion bigint NOT NULL
            );
            CREATE TABLE dbo.LegacyPaymentHistory
            (
                SessionId uniqueidentifier NOT NULL,
                EventId nvarchar(256) NOT NULL,
                Status nvarchar(64) NOT NULL,
                OccurredAt datetime2 NOT NULL,
                ProviderReference nvarchar(256) NULL
            );
            CREATE TABLE dbo.LegacyCallbacks
            (
                CallbackId nvarchar(200) NOT NULL,
                ProviderReference nvarchar(256) NOT NULL,
                ReceivedAt datetime2 NOT NULL,
                Payload nvarchar(max) NOT NULL,
                SourceSequence bigint NOT NULL
            );
            """, ("@merchant", MerchantId), ("@branch", BranchId), ("@sale", SaleId),
            ("@order", OrderId), ("@at", CapturedAt));
        await IntegrationDb.ExecAsync(connection, """
            INSERT dbo.LegacyHumans
                (LegacyId, Status, Provider, TenantId, ExternalUserId, EvidenceReference, MerchantId, SaleId,
                 BranchId, Email, DisplayName, PhoneNumber, SaleCode)
            VALUES
                (N'active-1', 1, N'entra', N'synthetic-tenant', N'active-sub', N'evidence-active', @merchant,
                 @sale, @branch, N'active@example.invalid', N'Active Synthetic', N'+66800000001', N'task9-sale'),
                (N'pending-1', 2, N'entra', N'synthetic-tenant', N'pending-sub', N'evidence-pending', @merchant,
                 @sale, @branch, N'pending@example.invalid', N'Pending Synthetic', N'+66800000002', N'task9-sale'),
                (N'rejected-1', 3, NULL, NULL, NULL, NULL, @merchant,
                 @sale, @branch, N'rejected@example.invalid', N'Rejected Synthetic', N'+66800000003', N'task9-sale');
            INSERT dbo.LegacyPaymentSessions
                (Id, OrderId, MerchantId, ProviderAccountReference, Environment, PspRequestReference,
                 PspTransactionReference, Amount, Currency, Status, SnapshotJson, ProviderAccountId,
                 CredentialVersionId, Provider, ConfigurationVersion)
            VALUES (@transaction, @order, @merchant, N'provider-account-synthetic', N'SANDBOX', N'request-backup-1',
                    N'charge-backup-1', 100.0000, N'THB', N'SUCCEEDED', N'{"legacy":true}', @providerAccount,
                    @credentialVersion, 1, 1);
            INSERT dbo.LegacyPaymentHistory
                (SessionId, EventId, Status, OccurredAt, ProviderReference)
            VALUES (@transaction, N'legacy-event-1', N'SUCCEEDED', @at, N'charge-backup-1');
            INSERT dbo.LegacyCallbacks
                (CallbackId, ProviderReference, ReceivedAt, Payload, SourceSequence)
            VALUES (N'legacy-callback-1', N'charge-backup-1', @at, N'{"status":"succeeded"}', 7);
            """, ("@merchant", MerchantId), ("@sale", SaleId), ("@branch", BranchId),
            ("@transaction", TransactionId), ("@order", OrderId), ("@providerAccount", ProviderAccountId),
            ("@credentialVersion", CredentialVersionId), ("@at", CapturedAt));
    }

    private static async Task<LegacyMigrationInput> ReadLegacyInputAsync(string database)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        var humans = new List<LegacyHuman>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT LegacyId, Status, Provider, TenantId, ExternalUserId, EvidenceReference, MerchantId,
                       SaleId, BranchId, Email, DisplayName, PhoneNumber, SaleCode
                FROM dbo.LegacyHumans ORDER BY LegacyId;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                IdentityEvidence? evidence = reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(4)
                    || reader.IsDBNull(5)
                    ? null
                    : new IdentityEvidence(reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
                humans.Add(new(
                    new LegacyKey("Human", reader.GetString(0)),
                    (LegacyHumanStatus)reader.GetInt32(1), evidence, reader.GetGuid(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(8) ? null : reader.GetGuid(8)));
            }
        }

        var histories = new Dictionary<Guid, List<LegacyPaymentHistory>>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT SessionId, EventId, Status, OccurredAt, ProviderReference FROM dbo.LegacyPaymentHistory;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!histories.TryGetValue(reader.GetGuid(0), out var events))
                    histories[reader.GetGuid(0)] = events = [];
                events.Add(new(reader.GetString(1), reader.GetString(2), reader.GetDateTime(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }
        }

        var sessions = new List<LegacyPaymentSession>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, OrderId, MerchantId, ProviderAccountReference, Environment, PspRequestReference,
                       PspTransactionReference, Amount, Currency, Status, SnapshotJson, ProviderAccountId,
                       CredentialVersionId, Provider, ConfigurationVersion
                FROM dbo.LegacyPaymentSessions;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetGuid(0);
                sessions.Add(new(id, reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetDecimal(7),
                    reader.GetString(8), reader.GetString(9), histories.GetValueOrDefault(id) ?? [], reader.GetString(10),
                    reader.GetGuid(11), reader.GetGuid(12), reader.GetInt32(13), reader.GetInt64(14)));
            }
        }

        var callbacks = new List<LegacyCallback>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT CallbackId, ProviderReference, ReceivedAt, Payload, SourceSequence FROM dbo.LegacyCallbacks;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                callbacks.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDateTime(2), reader.GetString(3), reader.GetInt64(4)));
        }

        return LegacyMigrationInput.Create(RunId, "synthetic-backup-task9-restored", humans, [], sessions, callbacks);
    }

    private static async Task BackupDatabaseAsync(string database)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(connection,
            $"BACKUP DATABASE [{database}] TO DISK = N'{BackupPath}' WITH COPY_ONLY, INIT, FORMAT;");
    }

    private static async Task RestoreDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await using var fileList = master.CreateCommand();
        fileList.CommandText = "RESTORE FILELISTONLY FROM DISK = @path;";
        fileList.Parameters.AddWithValue("@path", BackupPath);
        var files = new List<(string LogicalName, string Type)>();
        await using (var reader = await fileList.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                files.Add((reader.GetString(reader.GetOrdinal("LogicalName")), reader.GetString(reader.GetOrdinal("Type"))));
        }
        var data = files.Single(x => x.Type == "D").LogicalName.Replace("'", "''", StringComparison.Ordinal);
        var log = files.Single(x => x.Type == "L").LogicalName.Replace("'", "''", StringComparison.Ordinal);
        await using var restore = master.CreateCommand();
        restore.CommandText = $"RESTORE DATABASE [{database}] FROM DISK = @path WITH MOVE N'{data}' TO N'/var/opt/mssql/data/{database}.mdf', MOVE N'{log}' TO N'/var/opt/mssql/data/{database}_log.ldf', REPLACE;";
        restore.Parameters.AddWithValue("@path", BackupPath);
        await restore.ExecuteNonQueryAsync();
    }

    private static async Task DropDatabaseIfExistsAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        if (Convert.ToInt32(await IntegrationDb.ScalarAsync(master,
                "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", ("@db", database))) == 1)
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    private static async Task ResetDatabaseAsync()
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        if (Convert.ToInt32(await IntegrationDb.ScalarAsync(master,
                "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", ("@db", DatabaseName))) == 1)
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
        await IntegrationDb.ExecAsync(master, $"CREATE DATABASE [{DatabaseName}] COLLATE Thai_100_CI_AS;");
        await IntegrationDb.ExecAsync(master, $"ALTER DATABASE [{DatabaseName}] SET COMPATIBILITY_LEVEL = 170;");
        await using var target = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(target, "CREATE USER pol_app WITHOUT LOGIN;");
    }

    private static async Task DropDatabaseAsync()
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        if (Convert.ToInt32(await IntegrationDb.ScalarAsync(master,
                "SELECT CASE WHEN DB_ID(@db) IS NULL THEN 0 ELSE 1 END;", ("@db", DatabaseName))) == 1)
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}];");
    }
}
