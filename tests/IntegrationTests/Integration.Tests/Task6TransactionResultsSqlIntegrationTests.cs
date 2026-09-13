using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Persistence;
using Checkouts.Domain;
using Checkouts.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Payments.Application.Transactions;
using Platform.Application.Transactions;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Outbox;
using SharedKernel;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "CheckoutTransactions")]
[Collection("CheckoutTransactionsSql")]
public sealed class Task6TransactionResultsSqlIntegrationTests
{
    private const string DatabaseName = "PolCheckoutTransactionsTask6Test";
    private static readonly Guid MerchantId = IntegrationDb.MerchantA;
    private static readonly Guid OrderId = Guid.Parse("f0000000-0000-0000-0000-0000000000f1");
    private static readonly Guid FirstTransactionId = Guid.Parse("f0000000-0000-0000-0000-0000000000f2");
    private static readonly Guid SecondTransactionId = Guid.Parse("f0000000-0000-0000-0000-0000000000f3");
    private static readonly Guid MismatchTransactionId = Guid.Parse("f0000000-0000-0000-0000-0000000000f4");
    private static readonly Guid MismatchOrderId = Guid.Parse("f0000000-0000-0000-0000-0000000000f7");
    private static readonly Guid CancelledOrderId = Guid.Parse("f0000000-0000-0000-0000-0000000000f5");
    private static readonly Guid LateTransactionId = Guid.Parse("f0000000-0000-0000-0000-0000000000f6");
    private static readonly DateTime Now = new(2026, 9, 10, 19, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-8.4")]
    [Trait("Requirement", "REQ-8.6")]
    [Trait("Requirement", "REQ-8.7")]
    [Trait("Requirement", "REQ-8.9")]
    [Trait("Requirement", "REQ-8.10")]
    [Trait("Requirement", "REQ-8.11")]
    public async Task Sql_result_reducer_commits_success_once_duplicate_evidence_double_success_and_mismatch()
    {
        await ResetDatabaseAsync();
        try
        {
            var credential = Guid.NewGuid();
            var connection = NewConnection(credential);
            await MigrateAndSeedAsync(connection.Id, credential);
            var adapter = new ResultAdapter(PspChargeStatus.Paid, Money.Of(100m, "THB"));

            var first = await VerifyAsync(FirstTransactionId, connection, credential, adapter, "first-success");
            Assert.Equal(TransactionStatus.Succeeded, first.TransactionStatus);
            Assert.Equal(1, adapter.FetchCalls);

            await using (var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT PaymentStatus FROM shop.Orders WHERE Id = @order;
                    """, ("@order", OrderId))));
                Assert.Equal(FirstTransactionId.ToString(), Convert.ToString(await IntegrationDb.ScalarAsync(check, """
                    SELECT SuccessfulTransactionId FROM shop.Orders WHERE Id = @order;
                    """, ("@order", OrderId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.OutboxMessages
                    WHERE MerchantId = @merchant AND Type = N'payments.transaction-succeeded.v1';
                    """, ("@merchant", MerchantId))));
            }

            // A later provider success is retained as a second SUCCEEDED row but cannot move the canonical pointer.
            await InsertTransactionAsync(SecondTransactionId, OrderId, connection.Id, credential, 2, "charge-second");
            var second = await VerifyAsync(SecondTransactionId, connection, credential, adapter, "second-success");
            Assert.Equal(TransactionStatus.Succeeded, second.TransactionStatus);

            // Replay/out-of-order evidence is append-only and does not call PSP or emit another success event.
            var fetchesBeforeReplay = adapter.FetchCalls;
            var replay = await VerifyAsync(
                FirstTransactionId, connection, credential, adapter, "duplicate-success", PspChargeStatus.Pending, null);
            Assert.Equal(TransactionStatus.Succeeded, replay.TransactionStatus);
            Assert.Equal(fetchesBeforeReplay, adapter.FetchCalls);

            adapter.EventId = "callback-replay";
            await using (var callbackDb = CreateRuntimeDb(new IntegrationActor(MerchantId, Guid.NewGuid())))
            {
                var callbackRepository = new OrderRepository(callbackDb);
                var callbackService = new CheckoutTransactionService(
                    callbackRepository,
                    callbackRepository,
                    new FixedRoute(connection.Id, credential),
                    new FakeConnectionRepository(connection),
                    new FakeAdapterFactory(adapter),
                    new FakeVault(credential),
                    new MerchantRuntimeUnitOfWork(callbackDb, NoOpSecurityTelemetry.Instance),
                    new AlwaysNewIdempotencyStore(),
                    new EfOutbox(callbackDb, new FixedClock(Now), new IntegrationActor(MerchantId, Guid.NewGuid())),
                    new FixedClock(Now),
                    new TransactionInquiryScheduler(),
                    new FakeReturnBinding());
                var callback = new HandleTransactionWebhookHandler(
                    new FakeConnectionRepository(connection),
                    callbackRepository,
                    new FakeVault(credential),
                    new FakeAdapterFactory(adapter),
                    callbackService);
                var callbackResult = await callback.Handle(
                    new HandleTransactionWebhookCommand(connection.Id, "charge-first", "signature"), default);
                Assert.Equal(TransactionWebhookOutcome.Processed, callbackResult.Outcome);
            }

            await InsertTransactionAsync(MismatchTransactionId, MismatchOrderId, connection.Id, credential, 1, "charge-mismatch");
            var mismatch = await VerifyAsync(
                MismatchTransactionId, connection, credential, adapter, "amount-mismatch",
                PspChargeStatus.Paid, Money.Of(99m, "THB"));
            Assert.Equal(TransactionStatus.PendingConfirmation, mismatch.TransactionStatus);

            await using (var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName)))
            {
                Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.Transactions
                    WHERE OrderId = @order AND Status = 3;
                    """, ("@order", OrderId))));
                Assert.Equal(FirstTransactionId.ToString(), Convert.ToString(await IntegrationDb.ScalarAsync(check, """
                    SELECT SuccessfulTransactionId FROM shop.Orders WHERE Id = @order;
                    """, ("@order", OrderId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT NeedsReview FROM txn.Transactions WHERE Id = @transaction;
                    """, ("@transaction", SecondTransactionId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT NeedsReview FROM txn.Transactions WHERE Id = @transaction;
                    """, ("@transaction", MismatchTransactionId))));
                Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT PaymentStatus FROM shop.Orders WHERE Id = @order;
                    """, ("@order", MismatchOrderId))));
                Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.TransactionEvents
                    WHERE TransactionId = @transaction;
                    """, ("@transaction", FirstTransactionId))));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                    SELECT COUNT(*) FROM txn.OutboxMessages
                    WHERE MerchantId = @merchant AND Type = N'payments.transaction-succeeded.v1';
                    """, ("@merchant", MerchantId))));
            }

            await using var dueContext = CreateRuntimeDb(new IntegrationActor(MerchantId, Guid.NewGuid()));
            var due = await new OrderRepository(dueContext).ListDueAsync(Now.AddMinutes(2), 50, default);
            Assert.Contains(due, x => x.TransactionId == MismatchTransactionId && x.MerchantId == MerchantId);
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-8.8")]
    [Trait("Requirement", "REQ-8.12")]
    public async Task Sql_late_success_after_cancel_sets_paid_review_without_reopening_or_normal_outbox()
    {
        await ResetDatabaseAsync();
        try
        {
            var credential = Guid.NewGuid();
            var connection = NewConnection(credential);
            await MigrateAndSeedAsync(connection.Id, credential);
            await using var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            await IntegrationDb.ExecAsync(seed, """
                INSERT shop.Orders
                    (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                     IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                     CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                     SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                     OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
                VALUES (@order, @merchant, N'ORD6900000401', 6, 1, @at, @at, 2, 1, @at, @at,
                        NULL, NULL, N'Cancelled customer', '0800000000', 100.00, 'THB',
                        100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
                """, ("@order", CancelledOrderId), ("@merchant", MerchantId), ("@at", Now));
            await InsertTransactionAsync(LateTransactionId, CancelledOrderId, connection.Id, credential, 1, "charge-late");

            var adapter = new ResultAdapter(PspChargeStatus.Paid, Money.Of(100m, "THB"));
            var result = await VerifyAsync(LateTransactionId, connection, credential, adapter, "late-success");

            Assert.Equal(TransactionStatus.Succeeded, result.TransactionStatus);
            await using var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(6, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT Status FROM shop.Orders WHERE Id = @order;
                """, ("@order", CancelledOrderId))));
            Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT PaymentStatus FROM shop.Orders WHERE Id = @order;
                """, ("@order", CancelledOrderId))));
            Assert.Equal(LateTransactionId.ToString(), Convert.ToString(await IntegrationDb.ScalarAsync(check, """
                SELECT SuccessfulTransactionId FROM shop.Orders WHERE Id = @order;
                """, ("@order", CancelledOrderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT NeedsReview FROM txn.Transactions WHERE Id = @transaction;
                """, ("@transaction", LateTransactionId))));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.OutboxMessages
                WHERE MerchantId = @merchant AND Type = N'payments.transaction-succeeded.v1';
                """, ("@merchant", MerchantId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-8.5")]
    [Trait("Requirement", "REQ-8.12")]
    public async Task Sql_unreadable_pinned_credential_keeps_pending_review_without_failover_or_psp_call()
    {
        await ResetDatabaseAsync();
        try
        {
            var credential = Guid.NewGuid();
            var connection = NewConnection(credential);
            await MigrateAndSeedAsync(connection.Id, credential);
            var adapter = new ResultAdapter(PspChargeStatus.Paid, Money.Of(100m, "THB"));

            var result = await VerifyAsync(
                FirstTransactionId, connection, credential, adapter, "credential-unavailable",
                vaultUnavailable: true);

            Assert.Equal(TransactionStatus.PendingConfirmation, result.TransactionStatus);
            Assert.Equal(0, adapter.FetchCalls);
            await using var check = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT Status FROM txn.Transactions WHERE Id = @transaction;
                """, ("@transaction", FirstTransactionId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT NeedsReview FROM txn.Transactions WHERE Id = @transaction;
                """, ("@transaction", FirstTransactionId))));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(check, """
                SELECT COUNT(*) FROM txn.Transactions
                WHERE OrderId = @order AND ProviderAccountId <> @provider;
                """, ("@order", OrderId), ("@provider", connection.Id))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    private static async Task<CheckoutConfirmResult> VerifyAsync(
        Guid transactionId,
        Connection connection,
        Guid credential,
        ResultAdapter adapter,
        string eventReference,
        PspChargeStatus? result = null,
        Money? amount = null,
        bool vaultUnavailable = false)
    {
        if (result is not null)
            adapter.Result = new PspChargeConfirmation(result.Value, amount);
        await using var db = CreateRuntimeDb(new IntegrationActor(MerchantId, Guid.NewGuid()));
        var repository = new OrderRepository(db);
        var actor = new IntegrationActor(MerchantId, Guid.NewGuid());
        var service = new CheckoutTransactionService(
            repository,
            repository,
            new FixedRoute(connection.Id, credential),
            new FakeConnectionRepository(connection),
            new FakeAdapterFactory(adapter),
            new FakeVault(credential, vaultUnavailable),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            new AlwaysNewIdempotencyStore(),
            new EfOutbox(db, new FixedClock(Now), actor),
            new FixedClock(Now),
            new TransactionInquiryScheduler(),
            new FakeReturnBinding());
        return await service.VerifyAsync(MerchantId, transactionId, "webhook", eventReference, default);
    }

    private static Connection NewConnection(Guid credential)
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, "card", "task6/secret", Now);
        connection.SetInitialSecretVersion(credential, PspEnvironment.Sandbox);
        return connection;
    }

    private static async Task MigrateAndSeedAsync(Guid providerAccountId, Guid credential)
    {
        await using var migration = CreateMigrationContext();
        await migration.Database.MigrateAsync();
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.InsertMerchantAsync(connection, MerchantId, $"task6-result-{Guid.NewGuid():N}"[..24]);
        await IntegrationDb.ExecAsync(connection, """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                 IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                 CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
            VALUES (@order, @merchant, N'ORD6900000400', 8, 2, @at, @at, 3, 1, @at, @at,
                    NULL, NULL, N'Paid customer', '0800000000', 100.00, 'THB',
                    100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                 IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                 CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
            VALUES (@mismatchOrder, @merchant, N'ORD6900000402', 8, 2, @at, @at, 3, 1, @at, @at,
                    NULL, NULL, N'Mismatch customer', '0800000000', 100.00, 'THB',
                    100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
            """, ("@order", OrderId), ("@mismatchOrder", MismatchOrderId),
            ("@merchant", MerchantId), ("@at", Now));
        await InsertTransactionAsync(FirstTransactionId, OrderId, providerAccountId, credential, 1, "charge-first");
    }

    private static async Task InsertTransactionAsync(
        Guid id, Guid orderId, Guid providerAccountId, Guid credential, int attempt, string reference)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.ExecAsync(connection, """
                INSERT txn.Transactions
                    (Id, MerchantId, OrderId, TransactionNo, AttemptNo,
                     AmountAmount, AmountCurrency, PaymentMethod, Provider, ProviderAccountId,
                     Environment, CredentialVersionId, ConfigurationVersion,
                     ProviderRequestReference, ProviderReference, RedirectUrl, ReturnBinding,
                     Status, ProviderStatus, OrderSnapshot, SafeProviderMetadata, NeedsReview, ReviewCode,
                     CreatedAt, UpdatedAt, SucceededAt, LastInquiryAt, NextInquiryAt, InquiryAttempts, Version)
                VALUES (@id, @merchant, @order, @transactionNo, @attempt,
                        100.00, 'THB', 'card', 1, @provider, 1, @credential, 1,
                        @requestReference, @reference, N'https://psp.example/redirect', NULL,
                        1, 'redirect_created', N'{"schemaVersion":1,"provenance":"CAPTURED_AT_CONFIRM"}',
                        NULL, 0, NULL, @at, @at, NULL, NULL, NULL, 0, 1);
                """,
                ("@id", id), ("@merchant", MerchantId), ("@order", orderId),
                ("@transactionNo", $"TXN-{id:N}"), ("@attempt", attempt), ("@provider", providerAccountId),
                ("@credential", credential), ("@requestReference", $"request-{id:N}"),
                ("@reference", reference), ("@at", Now));
    }

    private static MerchantRuntimeDbContext CreateRuntimeDb(IActorContext actor) => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
            .Options,
        actor,
        AllowAllWriteAuthorizer.Instance,
        NoOpSecurityTelemetry.Instance);

    private static PolDbContext CreateMigrationContext() => new(
        new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(DatabaseName), sql => sql.UseCompatibilityLevel(170))
            .Options,
        new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration),
            typeof(Carts.Infrastructure.CartModuleRegistration),
            typeof(Orders.Infrastructure.OrdersModuleRegistration),
            typeof(Payments.Infrastructure.PaymentsModuleRegistration),
            typeof(global::Merchants.Infrastructure.MerchantsModuleRegistration),
            typeof(global::Admins.Infrastructure.AdminModuleRegistration),
            typeof(global::Iam.Infrastructure.IamModuleRegistration),
            typeof(global::Governance.Infrastructure.GovernanceModuleRegistration),
            typeof(global::Notifications.Infrastructure.NotificationsModuleRegistration),
            typeof(global::Accounts.Infrastructure.AccountsModuleRegistration),
            typeof(global::Access.Infrastructure.AccessModuleRegistration),
        ]));

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

    private sealed class ResultAdapter(PspChargeStatus status, Money? amount) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>(["card"]);
        public PspChargeConfirmation Result { get; set; } = new(status, amount);
        public int FetchCalls { get; private set; }
        public string EventId { get; set; } = "event";
        public Task<PspCharge> CreateRedirectChargeAsync(Session session, Guid pspConnectionId, string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
            Task.FromResult(new PspCharge("charge-created", "https://psp.example/redirect"));
        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
            Task.FromResult(new PspProbeResult("ok", "capture"));
        public bool VerifyWebhook(string rawPayload, string signature, string secret) => true;
        public WebhookEvent ParseWebhook(string rawPayload) => new(EventId, rawPayload, Result.Status);
        public PspWebhookReference ExtractWebhookReference(string rawPayload) => new(EventId, rawPayload);
        public Task<PspChargeConfirmation> FetchChargeAsync(string externalChargeId, string secret, PspEnvironment environment, CancellationToken cancellationToken)
        {
            FetchCalls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedRoute(Guid connectionId, Guid credential) : IPaymentRouteSelector
    {
        public Task<PspRouteSelection> SelectAsync(Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken) =>
            Task.FromResult(new PspRouteSelection(connectionId, Code.TwoCTwoP, credential, PspEnvironment.Sandbox));
    }

    private sealed class FakeConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) => Task.FromResult<Connection?>(connection);
        public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Connection?>(id == connection.Id ? connection : null);
        public void Add(Connection value) { }
        public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Connection>>([connection]);
    }

    private sealed class FakeAdapterFactory(IPspAdapter adapter) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => adapter;
    }

    private sealed class FakeVault(Guid credential, bool unavailable = false) : IVaultSecretStore
    {
        public Task<string> ReadVersionForServerAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
            versionId == credential && !unavailable ? Task.FromResult("secret") : throw new InvalidOperationException("pinned credential unavailable");
        public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeReturnBinding : ITransactionReturnBindingService
    {
        public string Issue(Guid transactionId, Guid orderId, string browserBindingId, DateTime expiresAt) => "binding";
        public bool TryRead(string value, out TransactionReturnBinding binding) { binding = default!; return false; }
    }

    private sealed class AlwaysNewIdempotencyStore : IIdempotencyStore
    {
        public Task<bool> TryBeginAsync(IReadOnlyCollection<string> keys, string context, CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class IntegrationActor(Guid merchantId, Guid userId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => userId;
        public bool HasActor => true;
        public string? SaleCode => null;
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
