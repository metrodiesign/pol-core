using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Idempotency;
using BuildingBlocks.Infrastructure.Persistence;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Application;
using Payments.Application.Ports;
using Payments.Application.Ports.Psp;
using Platform.Application.Transactions;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Idempotency;
using Persistence.MerchantRuntime.Orders;
using SharedKernel;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "CheckoutTransactions")]
[Collection("CheckoutTransactionsSql")]
public sealed class Task6CheckoutConfirmSqlIntegrationTests
{
    private const string DatabaseName = "PolCheckoutTransactionsTask6Test";
    private static readonly Guid MerchantId = IntegrationDb.MerchantA;
    private static readonly Guid OrderId = Guid.Parse("d0000000-0000-0000-0000-0000000000d1");
    private static readonly Guid LinkId = Guid.Parse("d0000000-0000-0000-0000-0000000000d2");
    private static readonly DateTime Now = new(2026, 9, 10, 17, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-7.6")]
    [Trait("Requirement", "REQ-7.8")]
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.2")]
    public async Task Two_tabs_with_different_idempotency_keys_commit_one_transaction_before_one_provider_call()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAndSeedAsync();
            var secretVersionId = Guid.NewGuid();
            var connection = NewConnection(secretVersionId);
            var adapter = new CaptureAdapter(IntegrationDb.SaConnFor(DatabaseName))
            {
                HoldFirstCall = true,
            };
            var route = new FixedRoute(connection.Id, secretVersionId);

            var first = RunConfirmAsync(
                "tab-a-key", connection, route, adapter, secretVersionId, Guid.NewGuid());
            await adapter.FirstCallObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var second = RunConfirmAsync(
                "tab-b-key", connection, route, adapter, secretVersionId, Guid.NewGuid());
            var secondError = await Record.ExceptionAsync(async () => await second);
            adapter.AllowFirstCall.TrySetResult();
            var firstResult = await first;

            Assert.Equal(1, adapter.CreateCalls);
            Assert.True(adapter.ObservedCommitted);
            Assert.Equal(TransactionStatus.Created, firstResult.TransactionStatus);
            var stale = Assert.IsType<ConflictException>(secondError);
            Assert.Equal("checkout_context_stale", stale.Code);
            Assert.Equal(1, route.Calls);

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM txn.Transactions WHERE MerchantId = @merchant AND OrderId = @order;
                """, ("@merchant", MerchantId), ("@order", OrderId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT PaymentStatus FROM shop.Orders WHERE Id = @order AND MerchantId = @merchant;
                """, ("@order", OrderId), ("@merchant", MerchantId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM txn.IdempotencyRecords
                WHERE MerchantId = @merchant AND Context = N'checkout.confirm';
                """, ("@merchant", MerchantId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-7.8")]
    [Trait("Requirement", "REQ-7.9")]
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.3")]
    public async Task Ambiguous_provider_result_is_pending_and_retry_reuses_the_same_reference_and_row()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAndSeedAsync();
            var secretVersionId = Guid.NewGuid();
            var connection = NewConnection(secretVersionId);
            var adapter = new CaptureAdapter(IntegrationDb.SaConnFor(DatabaseName))
            {
                ThrowAmbiguousOnFirstCall = true,
            };
            var route = new FixedRoute(connection.Id, secretVersionId);

            var first = await RunConfirmAsync(
                "ambiguous-key", connection, route, adapter, secretVersionId, Guid.NewGuid());
            Assert.Equal(TransactionStatus.PendingConfirmation, first.TransactionStatus);
            Assert.Equal(1, adapter.CreateCalls);
            var requestReference = adapter.RequestReferences.Single();

            var retry = await RunConfirmAsync(
                "retry-key", connection, route, adapter, secretVersionId, Guid.NewGuid(), orderVersion: 3);
            Assert.Equal(TransactionStatus.Created, retry.TransactionStatus);
            Assert.Equal(first.TransactionId, retry.TransactionId);
            Assert.Equal(2, adapter.CreateCalls);
            Assert.Equal(requestReference, adapter.RequestReferences[1]);
            Assert.Equal(1, route.Calls);

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM txn.Transactions WHERE MerchantId = @merchant AND OrderId = @order;
                """, ("@merchant", MerchantId), ("@order", OrderId))));
            Assert.Equal(requestReference, Convert.ToString(await IntegrationDb.ScalarAsync(verify, """
                SELECT ProviderRequestReference FROM txn.Transactions
                WHERE MerchantId = @merchant AND OrderId = @order;
                """, ("@merchant", MerchantId), ("@order", OrderId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-7.6")]
    [Trait("Requirement", "REQ-7.8")]
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.2")]
    public async Task Confirm_first_then_cancel_sees_the_same_potential_transaction_and_preserves_link()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAndSeedAsync();
            var secretVersionId = Guid.NewGuid();
            var connection = NewConnection(secretVersionId);
            var adapter = new CaptureAdapter(IntegrationDb.SaConnFor(DatabaseName)) { HoldFirstCall = true };
            var route = new FixedRoute(connection.Id, secretVersionId);
            var confirm = RunConfirmAsync(
                "confirm-before-cancel", connection, route, adapter, secretVersionId, Guid.NewGuid());
            await adapter.FirstCallObserved.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await using var cancelDb = CreateRuntimeDb(new IntegrationActor(MerchantId, null));
            var repository = new OrderRepository(cancelDb);
            var cancel = new CancelManagedOrderHandler(
                repository,
                repository,
                new PaymentSessionProbe(cancelDb),
                new EfIdempotencyStore(cancelDb, new FixedClock(Now), new IntegrationActor(MerchantId, null)),
                new MerchantRuntimeUnitOfWork(cancelDb, NoOpSecurityTelemetry.Instance),
                new FixedClock(Now));
            var cancellation = await Assert.ThrowsAsync<ConflictException>(() => cancel.Handle(
                new CancelManagedOrderCommand(
                    MerchantId, OrderId, ExpectedVersion: 3, "customer-abandoned", "cancel-during-provider"),
                CancellationToken.None).AsTask());
            Assert.Equal("payment_pending_verification", cancellation.Code);

            adapter.AllowFirstCall.TrySetResult();
            var confirmed = await confirm;
            Assert.Equal(TransactionStatus.Created, confirmed.TransactionStatus);
            Assert.Equal(1, adapter.CreateCalls);

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(8, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT Status FROM shop.Orders WHERE Id = @order;
                """, ("@order", OrderId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT PaymentStatus FROM shop.Orders WHERE Id = @order;
                """, ("@order", OrderId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT Status FROM checkout.PaymentLinks WHERE Id = @link;
                """, ("@link", LinkId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM txn.Transactions WHERE OrderId = @order;
                """, ("@order", OrderId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-7.6")]
    [Trait("Requirement", "REQ-7.7")]
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.2")]
    public async Task Cancel_first_order_lock_commit_prevents_confirm_from_creating_transaction_or_calling_provider()
    {
        await ResetDatabaseAsync();
        try
        {
            await MigrateAndSeedAsync();
            var secretVersionId = Guid.NewGuid();
            var connection = NewConnection(secretVersionId);
            var adapter = new CaptureAdapter(IntegrationDb.SaConnFor(DatabaseName));
            var route = new FixedRoute(connection.Id, secretVersionId);

            await using var lockConnection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            await using var lockTransaction = (SqlTransaction)await lockConnection.BeginTransactionAsync();
            await using (var lockCommand = lockConnection.CreateCommand())
            {
                lockCommand.Transaction = lockTransaction;
                lockCommand.CommandText = "SELECT Id FROM shop.Orders WITH (UPDLOCK,HOLDLOCK) WHERE Id = @id;";
                lockCommand.Parameters.AddWithValue("@id", OrderId);
                Assert.Equal(OrderId.ToString(), Convert.ToString(await lockCommand.ExecuteScalarAsync()));
            }

            var confirm = RunConfirmAsync(
                "confirm-after-cancel", connection, route, adapter, secretVersionId, Guid.NewGuid());
            await using (var cancelCommand = lockConnection.CreateCommand())
            {
                cancelCommand.Transaction = lockTransaction;
                cancelCommand.CommandText = """
                    UPDATE shop.Orders
                    SET Status = 6, PaymentStatus = 1, Version = 3, UpdatedAt = @at
                    WHERE Id = @order;
                    UPDATE checkout.PaymentLinks
                    SET Status = 2, RevokedAt = @at, Version = Version + 1
                    WHERE Id = @link;
                    """;
                cancelCommand.Parameters.AddWithValue("@order", OrderId);
                cancelCommand.Parameters.AddWithValue("@link", LinkId);
                cancelCommand.Parameters.AddWithValue("@at", Now);
                await cancelCommand.ExecuteNonQueryAsync();
            }
            await lockTransaction.CommitAsync();

            var refusal = await Assert.ThrowsAsync<AccessDeniedException>(() => confirm);
            Assert.Equal("checkout_link_revoked", refusal.Code);
            Assert.Equal(0, adapter.CreateCalls);

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            Assert.Equal(6, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT Status FROM shop.Orders WHERE Id = @order;
                """, ("@order", OrderId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT Status FROM checkout.PaymentLinks WHERE Id = @link;
                """, ("@link", LinkId))));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM txn.Transactions WHERE OrderId = @order;
                """, ("@order", OrderId))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    private static async Task<CheckoutConfirmResult> RunConfirmAsync(
        string idempotencyKey,
        Connection connection,
        FixedRoute route,
        CaptureAdapter adapter,
        Guid secretVersionId,
        Guid userId,
        long orderVersion = 2)
    {
        await using var db = CreateRuntimeDb(new IntegrationActor(MerchantId, userId));
        var repository = new OrderRepository(db);
        var service = new CheckoutTransactionService(
            repository,
            repository,
            route,
            new FakeConnectionRepository(connection),
            new FakeAdapterFactory(adapter),
            new FakeVault(secretVersionId),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            new EfIdempotencyStore(db, new FixedClock(Now), new IntegrationActor(MerchantId, userId)),
            new RecordingOutbox(),
            new FixedClock(Now),
            new TransactionInquiryScheduler(),
            new FakeReturnBinding());
        return await service.StartAsync(
            new CheckoutConfirmCommand(
                MerchantId, OrderId, orderVersion, "proof", "csrf", "card", idempotencyKey, "tab"),
            LinkId,
            "tab",
            CancellationToken.None);
    }

    private static Connection NewConnection(Guid secretVersionId)
    {
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, "card", "task6/secret", Now);
        connection.SetInitialSecretVersion(secretVersionId, PspEnvironment.Sandbox);
        return connection;
    }

    private static async Task MigrateAndSeedAsync()
    {
        await using var migration = CreateMigrationContext();
        await migration.Database.MigrateAsync();
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
        await IntegrationDb.InsertMerchantAsync(connection, MerchantId, $"task6-confirm-{Guid.NewGuid():N}"[..24]);
        await IntegrationDb.ExecAsync(connection, """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                 IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                 CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                 SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                 OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
            VALUES (@order, @merchant, N'ORD6900000201', 8, 1, @at, @at, 2, 1, @at, @at,
                    NULL, NULL, N'Task6 customer', '0800000000', 100.00, 'THB',
                    100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
            INSERT shop.OrderItems
                (Id, OrderId, MerchantId, Quantity, ProductCode, VariantCode, VariantName, Metadata,
                 DiscountAmount, DiscountCurrency, UnitPriceAmount, UnitPriceCurrency,
                 TaxAmount, TaxCurrency, LineAmount, LineCurrency)
            VALUES (@item, @order, @merchant, 1, N'DOC-1', 'VMI', N'Document', NULL,
                    0.00, 'THB', 100.00, 'THB', 0.00, 'THB', 100.00, 'THB');
            INSERT checkout.PaymentLinks
                (Id, OrderId, MerchantId, TokenHash, Status, CreatedAt, ExpiresAt, Version)
            VALUES (@link, @order, @merchant, @hash, 1, @at, DATEADD(hour, 1, @at), 1);
            """,
            ("@order", OrderId), ("@item", Guid.NewGuid()), ("@merchant", MerchantId),
            ("@link", LinkId), ("@hash", new byte[32]), ("@at", Now));
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

    private sealed class CaptureAdapter(string connectionString) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>(["card"]);
        public int CreateCalls { get; private set; }
        public bool HoldFirstCall { get; init; }
        public bool ThrowAmbiguousOnFirstCall { get; init; }
        public bool ObservedCommitted { get; private set; }
        public List<string> RequestReferences { get; } = [];
        public TaskCompletionSource FirstCallObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, PspEnvironment environment,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            RequestReferences.Add(session.Id.ToString("N"));
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.Status, t.OrderSnapshot, o.PaymentStatus
                FROM txn.Transactions t
                JOIN shop.Orders o ON o.Id = t.OrderId AND o.MerchantId = t.MerchantId
                WHERE t.Id = @id;
                """;
            command.Parameters.AddWithValue("@id", session.Id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Capture adapter could not observe committed Transaction.");
            ObservedCommitted = reader.GetInt32(0) == (int)TransactionStatus.Created
                && reader.GetInt32(2) == (int)PaymentStatus.Processing
                && reader.GetString(1).Contains("CAPTURED_AT_CONFIRM", StringComparison.Ordinal);
            FirstCallObserved.TrySetResult();
            if (ThrowAmbiguousOnFirstCall && CreateCalls == 1)
                throw new PspAmbiguousException("capture adapter simulated unknown response");
            if (HoldFirstCall && CreateCalls == 1)
                await AllowFirstCall.Task.WaitAsync(cancellationToken);
            return new PspCharge("capture-charge", "https://capture.example/redirect");
        }

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
            Task.FromResult(new PspProbeResult("ok", "capture"));
        public bool VerifyWebhook(string rawPayload, string signature, string secret) => true;
        public WebhookEvent ParseWebhook(string rawPayload) => new("event", rawPayload, PspChargeStatus.Pending);
        public PspWebhookReference ExtractWebhookReference(string rawPayload) => new("event", rawPayload);
        public Task<PspChargeConfirmation> FetchChargeAsync(string externalChargeId, string secret, PspEnvironment environment, CancellationToken cancellationToken) =>
            Task.FromResult(new PspChargeConfirmation(PspChargeStatus.Pending, null));
    }

    private sealed class FixedRoute(Guid connectionId, Guid secretVersionId) : IPaymentRouteSelector
    {
        public int Calls { get; private set; }
        public Task<PspRouteSelection> SelectAsync(Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PspRouteSelection(connectionId, Code.TwoCTwoP, secretVersionId, PspEnvironment.Sandbox));
        }
    }

    private sealed class FakeConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetAsync(Guid merchantId, Code psp, CancellationToken cancellationToken) => Task.FromResult<Connection?>(connection);
        public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Connection?>(id == connection.Id ? connection : null);
        public void Add(Connection value) { }
        public Task<IReadOnlyList<Connection>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Connection>>([connection]);
    }

    private sealed class FakeAdapterFactory(IPspAdapter adapter) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => adapter;
    }

    private sealed class FakeVault(Guid secretVersionId) : IVaultSecretStore
    {
        public Task<string> ReadVersionForServerAsync(Guid merchantId, Guid versionId, CancellationToken cancellationToken) =>
            versionId == secretVersionId ? Task.FromResult("capture-secret") : throw new InvalidOperationException();
        public Task StoreAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task InsertAsync(Guid merchantId, string name, string plaintextSecret, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeReturnBinding : ITransactionReturnBindingService
    {
        public string Issue(Guid transactionId, Guid orderId, string browserBindingId, DateTime expiresAt) => "return-binding";
        public bool TryRead(string value, out TransactionReturnBinding binding) { binding = default!; return false; }
    }

    private sealed class RecordingOutbox : IOutbox
    {
        public void Enqueue(Mediator.INotification notification) { }
    }

    private sealed class IntegrationActor(Guid merchantId, Guid? userId) : IActorContext
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
