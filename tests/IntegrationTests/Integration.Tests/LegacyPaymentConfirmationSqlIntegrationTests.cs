using BuildingBlocks.Application;
using Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Payments.Application.Capabilities;
using Payments.Application.Confirmation;
using Payments.Application.CreateSession;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Idempotency;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Payments;
using SharedKernel;

namespace Integration.Tests;

/// <summary>
/// Real SQL Server evidence for the legacy confirmation write set. A trigger fails the Session update after
/// the idempotency store has issued its own SaveChanges call; the surrounding UoW must roll the claim back
/// with Session and outbox. The retry then uses the same provider evidence and commits exactly once.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LegacyPaymentConfirmationSqlIntegrationTests
{
    private static readonly DateTime Now = new(2026, 9, 12, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Legacy_confirmation_failure_rolls_back_claim_session_and_outbox_and_retry_succeeds()
    {
        var database = $"pol_legacy_confirmation_{Guid.NewGuid():N}";
        var merchantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var connection = Connection.Create(merchantId, Code.TwoCTwoP, PaymentMethods.Card, "test/secret", Now);
        var secretVersionId = Guid.NewGuid();
        const string chargeId = "legacy-charge-rollback";
        var triggerInstalled = false;

        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using (var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(database))
                await migration.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync();

            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await IntegrationDb.InsertMerchantAsync(seed, merchantId, $"legacy-{Guid.NewGuid():N}"[..24]);
                await SeedSessionAsync(seed, merchantId, orderId, sessionId, connection.Id, secretVersionId, chargeId);
                await InstallFailureTriggerAsync(seed);
                triggerInstalled = true;
            }

            await using (var db = CreateRuntimeDb(database, merchantId))
            {
                var actor = new Actor(merchantId);
                var sessions = new SessionRepository(db);
                var service = CreateService(db, sessions, connection, actor);
                var session = await sessions.GetByIdAsync(sessionId, default)
                    ?? throw new InvalidOperationException("Seeded payment session was not readable.");

                await Assert.ThrowsAnyAsync<Exception>(() => service.ConfirmAsync(session, default));
            }

            await using (var drop = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await DropFailureTriggerAsync(drop);
                triggerInstalled = false;
            }

            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(0, await CountAsync(verify, "txn.IdempotencyRecords", merchantId));
                Assert.Equal(0, await CountAsync(verify, "txn.OutboxMessages", merchantId));
                Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify,
                    "SELECT Status FROM txn.PaymentSessions WHERE Id = @id;", ("@id", sessionId))));
            }

            await using (var retryDb = CreateRuntimeDb(database, merchantId))
            {
                var actor = new Actor(merchantId);
                var sessions = new SessionRepository(retryDb);
                var service = CreateService(retryDb, sessions, connection, actor);
                var session = await sessions.GetByIdAsync(sessionId, default)
                    ?? throw new InvalidOperationException("Seeded payment session disappeared after rollback.");

                Assert.Equal(ConfirmationOutcome.Paid, await service.ConfirmAsync(session, default));
            }

            await using var afterRetry = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(1, await CountAsync(afterRetry, "txn.IdempotencyRecords", merchantId));
            Assert.Equal(1, await CountAsync(afterRetry, "txn.OutboxMessages", merchantId));
            Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry,
                "SELECT Status FROM txn.PaymentSessions WHERE Id = @id;", ("@id", sessionId))));
        }
        finally
        {
            if (triggerInstalled)
            {
                await using var cleanup = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
                await DropFailureTriggerAsync(cleanup);
            }

            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Stale_session_replacement_failure_rolls_back_terminal_claim_event_outbox_and_replacement_then_retries()
    {
        var database = $"pol_legacy_replacement_{Guid.NewGuid():N}";
        var merchantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var staleSessionId = Guid.NewGuid();
        var connection = Connection.Create(merchantId, Code.TwoCTwoP, PaymentMethods.Card, "test/secret", Now);
        var secretVersionId = Guid.NewGuid();
        const string chargeId = "legacy-charge-expiry";
        var replacementTriggerInstalled = false;

        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using (var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(database))
                await migration.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync();

            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await IntegrationDb.InsertMerchantAsync(seed, merchantId, $"legacy-replacement-{Guid.NewGuid():N}"[..24]);
                await SeedOrderAsync(seed, merchantId, orderId, staleSessionId);
                await SeedSessionAsync(
                    seed, merchantId, orderId, staleSessionId, connection.Id, secretVersionId, chargeId,
                    createdAt: Now.AddHours(-25));
                await InstallReplacementFailureTriggerAsync(seed);
                replacementTriggerInstalled = true;
            }

            await using (var db = CreateRuntimeDb(database, merchantId))
            {
                var actor = new Actor(merchantId);
                var sessions = new SessionRepository(db);
                var confirmation = CreateService(
                    db,
                    sessions,
                    connection,
                    actor,
                    new ScriptedAdapter(PspChargeStatus.Pending));
                var handler = CreateSessionHandler(db, sessions, confirmation, connection, secretVersionId, actor);

                var failure = await Record.ExceptionAsync(async () =>
                    await handler.Handle(
                        new CreateSessionCommand(orderId, merchantId, PaymentMethods.Card),
                        default));

                Assert.IsType<DbUpdateException>(failure);
                Assert.Null(db.Database.CurrentTransaction);
            }

            await using (var afterFailure = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure,
                    "SELECT Status FROM txn.PaymentSessions WHERE Id = @id;", ("@id", staleSessionId))));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterFailure,
                    "SELECT COUNT(*) FROM txn.PaymentSessions WHERE OrderId = @orderId AND Id <> @stale;",
                    ("@orderId", orderId), ("@stale", staleSessionId))));
                Assert.Equal(staleSessionId.ToString(), Convert.ToString(await IntegrationDb.ScalarAsync(afterFailure,
                    "SELECT PaymentSessionId FROM shop.Orders WHERE Id = @id;", ("@id", orderId))));
                Assert.Equal(0, await CountAsync(afterFailure, "txn.IdempotencyRecords", merchantId));
                Assert.Equal(0, await CountAsync(afterFailure, "txn.OutboxMessages", merchantId));
            }

            await using (var drop = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await DropReplacementFailureTriggerAsync(drop);
                replacementTriggerInstalled = false;
            }

            Guid replacementSessionId;
            await using (var retryDb = CreateRuntimeDb(database, merchantId))
            {
                var actor = new Actor(merchantId);
                var sessions = new SessionRepository(retryDb);
                var confirmation = CreateService(
                    retryDb,
                    sessions,
                    connection,
                    actor,
                    new ScriptedAdapter(PspChargeStatus.Pending));
                var handler = CreateSessionHandler(retryDb, sessions, confirmation, connection, secretVersionId, actor);

                replacementSessionId = (await handler.Handle(
                    new CreateSessionCommand(orderId, merchantId, PaymentMethods.Card),
                    default)).PaymentSessionId;
            }

            Assert.NotEqual(staleSessionId, replacementSessionId);
            await using var afterRetry = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(5, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry,
                "SELECT Status FROM txn.PaymentSessions WHERE Id = @id;", ("@id", staleSessionId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry,
                "SELECT Status FROM txn.PaymentSessions WHERE Id = @id;", ("@id", replacementSessionId))));
            Assert.Equal(replacementSessionId.ToString(), Convert.ToString(await IntegrationDb.ScalarAsync(afterRetry,
                "SELECT PaymentSessionId FROM shop.Orders WHERE Id = @id;", ("@id", orderId))));
            Assert.Equal(0, await CountAsync(afterRetry, "txn.IdempotencyRecords", merchantId));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(afterRetry, """
                SELECT COUNT(*) FROM txn.OutboxMessages
                WHERE MerchantId = @merchant AND Type = @type AND SchemaVersion = N'v1';
                """, ("@merchant", merchantId), ("@type", PaymentExpired.EventType))));
        }
        finally
        {
            if (replacementTriggerInstalled)
            {
                await using var cleanup = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
                await DropReplacementFailureTriggerAsync(cleanup);
            }

            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Legacy_confirmation_cancellation_and_ambiguous_provider_failure_leave_transaction_disposed_and_unclaimed()
    {
        var database = $"pol_legacy_failure_{Guid.NewGuid():N}";
        var merchantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var connection = Connection.Create(merchantId, Code.TwoCTwoP, PaymentMethods.Card, "test/secret", Now);
        var secretVersionId = Guid.NewGuid();
        const string chargeId = "legacy-charge-ambiguous";
        var triggerInstalled = false;

        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using (var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(database))
                await migration.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().MigrateAsync();

            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await IntegrationDb.InsertMerchantAsync(seed, merchantId, $"legacy-failure-{Guid.NewGuid():N}"[..24]);
                await SeedSessionAsync(seed, merchantId, orderId, sessionId, connection.Id, secretVersionId, chargeId);
            }

            await using (var cancelledDb = CreateRuntimeDb(database, merchantId))
            {
                var actor = new Actor(merchantId);
                var sessions = new SessionRepository(cancelledDb);
                var service = CreateService(
                    cancelledDb,
                    sessions,
                    connection,
                    actor,
                    new ScriptedAdapter(PspChargeStatus.Paid));
                var session = await sessions.GetByIdAsync(sessionId, default)
                    ?? throw new InvalidOperationException("Seeded payment session was not readable.");
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                var failure = await Record.ExceptionAsync(() => service.ConfirmAsync(session, cancellation.Token));

                Assert.IsType<OperationCanceledException>(failure);
                Assert.Null(cancelledDb.Database.CurrentTransaction);
            }

            await using (var ambiguousDb = CreateRuntimeDb(database, merchantId))
            {
                var actor = new Actor(merchantId);
                var sessions = new SessionRepository(ambiguousDb);
                var service = CreateService(
                    ambiguousDb,
                    sessions,
                    connection,
                    actor,
                    new ScriptedAdapter(fetchFailure: new TimeoutException("ambiguous PSP result")));
                var session = await sessions.GetByIdAsync(sessionId, default)
                    ?? throw new InvalidOperationException("Seeded payment session disappeared after cancellation.");

                var failure = await Record.ExceptionAsync(() => service.ConfirmAsync(session, default));

                Assert.IsType<TimeoutException>(failure);
                Assert.Null(ambiguousDb.Database.CurrentTransaction);
            }

            await using (var install = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await InstallFailureTriggerAsync(install);
                triggerInstalled = true;
            }

            await using (var applyDb = CreateRuntimeDb(database, merchantId))
            {
                var actor = new Actor(merchantId);
                var sessions = new SessionRepository(applyDb);
                var service = CreateService(
                    applyDb,
                    sessions,
                    connection,
                    actor,
                    new ScriptedAdapter(PspChargeStatus.Paid));
                var session = await sessions.GetByIdAsync(sessionId, default)
                    ?? throw new InvalidOperationException("Seeded payment session disappeared after ambiguous failure.");

                var failure = await Record.ExceptionAsync(() => service.ConfirmAsync(session, default));

                Assert.IsType<DbUpdateException>(failure);
                Assert.Null(applyDb.Database.CurrentTransaction);
            }

            await using (var drop = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                await DropFailureTriggerAsync(drop);
                triggerInstalled = false;
            }

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify,
                "SELECT Status FROM txn.PaymentSessions WHERE Id = @id;", ("@id", sessionId))));
            Assert.Equal(0, await CountAsync(verify, "txn.IdempotencyRecords", merchantId));
            Assert.Equal(0, await CountAsync(verify, "txn.OutboxMessages", merchantId));
        }
        finally
        {
            if (triggerInstalled)
            {
                await using var cleanup = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
                await DropFailureTriggerAsync(cleanup);
            }

            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static PaymentConfirmationService CreateService(
        CommerceDbContext db,
        ISessionRepository sessions,
        Connection connection,
        IActorContext actor,
        IPspAdapter? adapter = null)
    {
        var clock = new FixedClock();
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        var adapters = new FixedAdapterFactory(adapter ?? new ScriptedAdapter(PspChargeStatus.Paid));
        return new PaymentConfirmationService(
            new FixedConnectionRepository(connection),
            adapters,
            new FixedVault(),
            new EfIdempotencyStore(db, clock, actor),
            new EfOutbox(db, clock, actor),
            uow,
            sessions,
            clock,
            NullLogger<PaymentConfirmationService>.Instance);
    }

    private static CreateSessionHandler CreateSessionHandler(
        CommerceDbContext db,
        ISessionRepository sessions,
        PaymentConfirmationService confirmation,
        Connection connection,
        Guid secretVersionId,
        IActorContext actor)
    {
        var clock = new FixedClock();
        var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        return new CreateSessionHandler(
            new PayableOrderReader(db),
            sessions,
            confirmation,
            new NoDocumentSales(),
            uow,
            clock,
            new NoOpAuthorizationLocks(),
            new AllowCapabilities(),
            new FixedRoute(connection.Id, secretVersionId));
    }

    private static CommerceDbContext CreateRuntimeDb(string database, Guid merchantId) => new(
        new DbContextOptionsBuilder<CommerceDbContext>()
            .UseSqlServer(IntegrationDb.AppConnFor(database), sql => sql.UseCompatibilityLevel(170))
            .Options,
        new Actor(merchantId),
        AllowAllWrites.Instance,
        NoOpSecurityTelemetry.Instance);

    private static Task SeedSessionAsync(
        SqlConnection sql,
        Guid merchantId,
        Guid orderId,
        Guid sessionId,
        Guid connectionId,
        Guid secretVersionId,
        string chargeId,
        DateTime? createdAt = null) =>
        IntegrationDb.ExecAsync(sql, """
            INSERT txn.PaymentSessions
                (Id, MerchantId, OrderId, Method, Psp, PspConnectionId, SecretVersionId, PspEnvironment,
                 RoutingSnapshotVersion, Status, PspExternalChargeId, RedirectUrl, CreatedAt, UpdatedAt,
                 AmountAmount, AmountCurrency, Version)
            VALUES
                (@id, @merchant, @order, N'card', 1, @connection, @secret, 1, 1, 2, @charge,
                 N'https://psp.test/legacy', @at, @at, 100.00, N'THB', 1);
            """,
            ("@id", sessionId), ("@merchant", merchantId), ("@order", orderId),
            ("@connection", connectionId), ("@secret", secretVersionId), ("@charge", chargeId),
            ("@at", createdAt ?? Now));

    private static Task SeedOrderAsync(SqlConnection sql, Guid merchantId, Guid orderId, Guid sessionId) =>
        IntegrationDb.ExecAsync(sql, """
            INSERT shop.Orders
                (Id, MerchantId, OriginatorId, InitiatingAudience, OrderNo, Status, PaymentStatus,
                 CreatedAt, UpdatedAt, Version, IsFrozen, IssuedAt, FrozenAt, SummaryToken,
                 SummaryTokenExpiresAt, CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentSessionId, PaymentChannel, BusinessType)
            VALUES
                (@id, @merchant, @originator, 2, @orderNo, 1, 1, @at, @at, 1, 0, NULL, NULL,
                 @summary, DATEADD(hour, 72, @at), N'Legacy customer', '0800000000', 100.00, N'THB',
                 100.00, N'THB', 0.00, N'THB', 0.00, N'THB', @session, N'card', N'insurance');
            """,
            ("@id", orderId), ("@merchant", merchantId), ("@originator", merchantId),
            ("@orderNo", $"ORD{Guid.NewGuid():N}"[..13]), ("@at", Now),
            ("@summary", $"legacy-summary-{Guid.NewGuid():N}"), ("@session", sessionId));

    private static Task InstallFailureTriggerAsync(SqlConnection sql) =>
        InstallFailureTriggerAsync(
            sql,
            "TR_TestLegacyConfirmationFailure",
            "UPDATE",
            "legacy confirmation failure injection");

    private static Task InstallReplacementFailureTriggerAsync(SqlConnection sql) =>
        InstallFailureTriggerAsync(
            sql,
            "TR_TestLegacyReplacementFailure",
            "INSERT",
            "legacy replacement failure injection");

    private static Task InstallFailureTriggerAsync(
        SqlConnection sql,
        string triggerName,
        string operation,
        string message) =>
        IntegrationDb.ExecAsync(sql, $"""
            CREATE TRIGGER txn.[{triggerName}]
            ON txn.PaymentSessions
            AFTER {operation}
            AS
            BEGIN
                SET NOCOUNT ON;
                THROW 51000, '{message}', 1;
            END;
            """);

    private static Task DropFailureTriggerAsync(SqlConnection sql) =>
        DropFailureTriggerAsync(sql, "TR_TestLegacyConfirmationFailure");

    private static Task DropReplacementFailureTriggerAsync(SqlConnection sql) =>
        DropFailureTriggerAsync(sql, "TR_TestLegacyReplacementFailure");

    private static Task DropFailureTriggerAsync(SqlConnection sql, string triggerName) =>
        IntegrationDb.ExecAsync(sql, $"""
            IF OBJECT_ID(N'txn.{triggerName}', N'TR') IS NOT NULL
                DROP TRIGGER txn.[{triggerName}];
            """);

    private static async Task<int> CountAsync(SqlConnection sql, string table, Guid merchantId) =>
        Convert.ToInt32(await IntegrationDb.ScalarAsync(sql,
            $"SELECT COUNT(*) FROM {table} WHERE MerchantId = @merchant;", ("@merchant", merchantId)));

    private sealed class FixedConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) =>
            Task.FromResult<Connection?>(merchantId == connection.MerchantId && psp == connection.Psp ? connection : null);
        public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<Connection?>(id == connection.Id ? connection : null);
        public void Add(Connection value) => throw new NotSupportedException();
        public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Connection>>([connection]);
    }

    private sealed class FixedAdapterFactory(IPspAdapter adapter) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => adapter;
    }

    private sealed class ScriptedAdapter(
        PspChargeStatus? status = null,
        Exception? fetchFailure = null) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>([PaymentMethods.Card]);
        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, PspEnvironment environment,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
            FetchAsync(cancellationToken);

        private Task<PspChargeConfirmation> FetchAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fetchFailure is not null)
                return Task.FromException<PspChargeConfirmation>(fetchFailure);
            return Task.FromResult(new PspChargeConfirmation(
                status ?? throw new InvalidOperationException("Scripted PSP status is required."),
                Money.Of(100, "THB")));
        }
        public bool VerifyWebhook(string rawPayload, string signature, string secret) => throw new NotSupportedException();
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }

    private sealed class FixedVault : IVaultSecretStore
    {
        public Task<string> ReadVersionForServerAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
            Task.FromResult("test-secret");
        public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            Task.FromResult("test-secret");
        public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Actor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FixedRoute(Guid connectionId, Guid secretVersionId) : IPaymentRouteSelector
    {
        public Task<PspRouteSelection> SelectAsync(
            Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken) =>
            Task.FromResult(new PspRouteSelection(
                connectionId, Code.TwoCTwoP, secretVersionId, PspEnvironment.Sandbox));
    }

    private sealed class NoDocumentSales : IDocumentSaleProbe
    {
        public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
            IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentSaleStatus>>([]);
    }

    private sealed class NoOpAuthorizationLocks : IPaymentAuthorizationLockManager
    {
        public Task AcquireGlobalExclusiveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcquireMerchantSharedAsync(Guid merchantId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AcquireMerchantExclusiveAsync(Guid merchantId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AllowCapabilities : IEffectivePaymentCapabilityResolver
    {
        public Task<PaymentMethodDecision> ResolveMethodAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) =>
            Task.FromResult(new PaymentMethodDecision(true, request.Method, PaymentCapabilityDenial.None, Guid.NewGuid()));

        public Task<IReadOnlyList<EffectivePaymentMethod>> ListMethodsAsync(
            PaymentCapabilitySubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentMethod>>([]);

        public Task<IReadOnlyList<EffectivePaymentOption>> ResolveOptionsAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentOption>>([]);
    }

    private sealed class AllowAllWrites : IWriteAuthorizer
    {
        public static readonly AllowAllWrites Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
