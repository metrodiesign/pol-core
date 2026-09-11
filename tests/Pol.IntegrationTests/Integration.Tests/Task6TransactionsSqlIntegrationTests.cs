using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Payments.Domain.Psp;
using Integration.Tests;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "CheckoutTransactions")]
[Collection("CheckoutTransactionsSql")]
public sealed class Task6TransactionsSqlIntegrationTests
{
    private const string DatabaseName = "PolCheckoutTransactionsTask6Test";

    [Fact]
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.2")]
    [Trait("Requirement", "REQ-8.9")]
    public async Task Fresh_chain_preserves_task2_to_task5_history_and_enforces_transaction_schema()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var context = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await context.Database.MigrateAsync();
            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));

            var applied = await ReadStringsAsync(connection, "SELECT MigrationId FROM dbo.__EFMigrationsHistory;");
            Assert.Contains("20260910021908_Task2IdentityAccess", applied);
            Assert.Contains("20260910035334_Task3MerchantMaster", applied);
            Assert.Contains("20260910044722_Task4AgentRegistration", applied);
            Assert.Contains("20260910060757_Task5OrdersLinks", applied);
            Assert.Contains(applied, id => id.EndsWith("_Task6Transactions", StringComparison.Ordinal));
            Assert.Equal(context.Database.GetMigrations().Count(), applied.Count);

            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT OBJECT_ID(N'txn.Transactions', N'U');") is not null));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT OBJECT_ID(N'txn.TransactionEvents', N'U');") is not null));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM sys.columns
                WHERE object_id = OBJECT_ID(N'shop.Orders') AND name = N'SuccessfulTransactionId'
                  AND is_nullable = 1;
                """)));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM sys.tables
                WHERE name IN (N'Payment', N'Payments') AND schema_id = SCHEMA_ID(N'txn');
                """)));

            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM sys.foreign_keys
                WHERE name = N'FK_Transactions_Orders_OrderId_MerchantId';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM sys.foreign_keys
                WHERE name = N'FK_TransactionEvents_Transactions_TransactionId_MerchantId';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM sys.foreign_keys
                WHERE name = N'FK_Orders_Transactions_SuccessfulTransactionId_MerchantId';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT CASE WHEN is_unique = 1 AND filter_definition LIKE N'%Status%' THEN 1 ELSE 0 END
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'txn.Transactions') AND name = N'IX_Transactions_OrderId_Potential';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT CASE WHEN is_unique = 1 THEN 1 ELSE 0 END
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'txn.Transactions')
                  AND name = N'IX_Transactions_OrderId_AttemptNo';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT CASE WHEN is_unique = 1 THEN 1 ELSE 0 END
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'txn.Transactions')
                  AND name = N'IX_Transactions_ProviderAccountId_Environment_ProviderRequestReference';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT CASE WHEN is_unique = 1 THEN 1 ELSE 0 END
                FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'txn.TransactionEvents')
                  AND name = N'IX_TransactionEvents_TransactionId_EventReference_Source';
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT CASE WHEN c.max_length = -1 AND t.name = N'nvarchar' THEN 1 ELSE 0 END
                FROM sys.columns c
                JOIN sys.types t ON t.user_type_id = c.user_type_id
                WHERE c.object_id = OBJECT_ID(N'txn.Transactions') AND c.name = N'OrderSnapshot';
                """)));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-8.1")]
    [Trait("Requirement", "REQ-8.2")]
    [Trait("Requirement", "REQ-8.9")]
    public async Task Sql_allows_two_successes_but_rejects_two_potential_transactions_and_invalid_pointer()
    {
        await ResetDatabaseAsync();
        try
        {
            await using var migration = CreateContext(IntegrationDb.SaConnFor(DatabaseName));
            await migration.Database.MigrateAsync();
            var orderId = Guid.NewGuid();
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var thirdId = Guid.NewGuid();
            var fourthId = Guid.NewGuid();
            var provider = Guid.NewGuid();
            var now = new DateTime(2026, 9, 10, 16, 0, 0, DateTimeKind.Utc);
            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(DatabaseName));
            await IntegrationDb.InsertMerchantAsync(connection, IntegrationDb.MerchantA,
                $"task6-{Guid.NewGuid():N}"[..24]);
            await IntegrationDb.ExecAsync(connection, """
                INSERT shop.Orders
                    (Id, MerchantId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                     IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                     CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                     SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                     OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
                VALUES
                    (@order, @merchant, @orderNo, 8, 1, @at, @at, 2, 1, @at, @at,
                     NULL, NULL, N'Task6 customer', '0800000000', 250.00, 'THB',
                     250.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
                """,
                ("@order", orderId), ("@merchant", IntegrationDb.MerchantA),
                ("@orderNo", $"ORD69{Random.Shared.Next(10_000_000, 99_999_999)}"), ("@at", now));

            await InsertTransactionAsync(connection, firstId, orderId, provider, 1, 3, "charge-first", now);
            await InsertTransactionAsync(connection, secondId, orderId, provider, 2, 3, "charge-second", now.AddMinutes(1));
            await IntegrationDb.ExecAsync(connection, """
                UPDATE shop.Orders SET SuccessfulTransactionId = @transaction WHERE Id = @order;
                """, ("@transaction", firstId), ("@order", orderId));

            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                SELECT COUNT(*) FROM txn.Transactions
                WHERE OrderId = @order AND Status = 3;
                """, ("@order", orderId))));

            await InsertTransactionAsync(
                connection, thirdId, orderId, provider, 3, 1, "charge-third", now.AddMinutes(2));
            await Assert.ThrowsAsync<SqlException>(() => InsertTransactionAsync(
                connection, fourthId, orderId, provider, 4, 2, "charge-fourth", now.AddMinutes(3)));

            var invalidPointer = Guid.NewGuid();
            await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(connection, """
                UPDATE shop.Orders SET SuccessfulTransactionId = @transaction WHERE Id = @order;
                """, ("@transaction", invalidPointer), ("@order", orderId)));

            await IntegrationDb.ExecAsync(connection, """
                INSERT txn.TransactionEvents
                    (Id, MerchantId, TransactionId, Source, EventReference, Status,
                     ProviderStatus, EvidenceCode, SafeDetails, OccurredAt, ReceivedAt)
                VALUES (@id, @merchant, @transaction, 'webhook', 'event-1', 3,
                        'paid', NULL, NULL, @at, @at);
                """,
                ("@id", Guid.NewGuid()), ("@merchant", IntegrationDb.MerchantA),
                ("@transaction", firstId), ("@at", now));
            await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(connection, """
                INSERT txn.TransactionEvents
                    (Id, MerchantId, TransactionId, Source, EventReference, Status,
                     ProviderStatus, EvidenceCode, SafeDetails, OccurredAt, ReceivedAt)
                VALUES (@id, @merchant, @transaction, 'webhook', 'event-1', 3,
                        'paid', NULL, NULL, @at, @at);
                """,
                ("@id", Guid.NewGuid()), ("@merchant", IntegrationDb.MerchantA),
                ("@transaction", firstId), ("@at", now.AddMinutes(1))));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    private static async Task InsertTransactionAsync(
        SqlConnection connection,
        Guid id,
        Guid orderId,
        Guid provider,
        int attempt,
        int status,
        string reference,
        DateTime at)
    {
        await IntegrationDb.ExecAsync(connection, """
            INSERT txn.Transactions
                (Id, MerchantId, OrderId, TransactionNo, AttemptNo,
                 AmountAmount, AmountCurrency, PaymentMethod, Provider, ProviderAccountId,
                 Environment, CredentialVersionId, ConfigurationVersion,
                 ProviderRequestReference, ProviderReference, RedirectUrl, ReturnBinding,
                 Status, ProviderStatus, OrderSnapshot, SafeProviderMetadata, NeedsReview, ReviewCode,
                 CreatedAt, UpdatedAt, SucceededAt, LastInquiryAt, NextInquiryAt, InquiryAttempts, Version)
            VALUES
                (@id, @merchant, @order, @transactionNo, @attempt,
                 250.00, 'THB', 'card', 1, @provider,
                 1, @credential, 1, @requestReference, @reference, NULL, NULL,
                 @status, CASE WHEN @status = 3 THEN 'paid' ELSE 'created' END,
                 N'{"schemaVersion":1,"provenance":"CAPTURED_AT_CONFIRM"}', NULL, 0, NULL,
                 @at, @at, CASE WHEN @status = 3 THEN @at ELSE NULL END,
                 NULL, NULL, 0, 1);
            """,
            ("@id", id), ("@merchant", IntegrationDb.MerchantA), ("@order", orderId),
            ("@transactionNo", $"TXN-{id:N}"), ("@attempt", attempt), ("@provider", provider),
            ("@credential", Guid.NewGuid()), ("@requestReference", $"request-{id:N}"),
            ("@reference", reference), ("@status", status), ("@at", at));
    }

    private static PolDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(connectionString, sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new PolDbContext(options, new ModuleAssemblies([
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
    }

    private static async Task<List<string>> ReadStringsAsync(SqlConnection connection, string sql)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
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
