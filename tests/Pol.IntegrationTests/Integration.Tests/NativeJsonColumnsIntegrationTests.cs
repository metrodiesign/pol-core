using Microsoft.Data.SqlClient;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class NativeJsonColumnsIntegrationTests
{
    [Fact]
    public async Task Exactly_nine_snapshot_authoritative_columns_are_native_json()
    {
        await using var database = await FreshJsonDatabase.CreateAsync();
        var connection = database.Connection;
        const string sql = """
            SELECT STRING_AGG(CONCAT(s.name, '.', t.name, '.', c.name), ',')
                   WITHIN GROUP (ORDER BY s.name, t.name, c.name)
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE ty.name = N'json';
            """;

        Assert.Equal(
            "acct.AgentRegistrationAttempts.ProfileJson,acct.AgentRegistrations.ProfileJson,acct.Agents.Metadata,acct.Employees.Metadata,admin.ProvisioningOperations.Result,merch.Merchants.Metadata,merch.UserOutbox.Payload,shop.CartItems.Metadata,shop.OrderItems.Metadata",
            await IntegrationDb.ScalarAsync(connection, sql));
    }

    [Fact]
    public async Task Every_native_json_column_accepts_valid_and_rejects_invalid_json()
    {
        await using var database = await FreshJsonDatabase.CreateAsync();
        var connection = database.Connection;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        var merchantId = Guid.NewGuid();
        var cartId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        await ExecuteAsync(connection, transaction, """
            INSERT merch.Merchants (Id, Code, Name, Note, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
            VALUES (@merchant, @code, N'JSON probe', NULL, 1, N'TH', N'THB', N'card', SYSUTCDATETIME(), @json);
            INSERT shop.Carts (Id, MerchantId, SaleCode, Status, CreatedAt, Version)
            VALUES (@cart, @merchant, '77001', N'Open', SYSUTCDATETIME(), 0);
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, SaleCode, PaymentSessionId, Status, PaymentStatus, CreatedAt, PaidAt,
                 SummaryToken, SummaryTokenExpiresAt, NotificationRecipient, PaymentChannel,
                 CustomerName, CustomerPhone, CustomerEmail, AmountAmount, AmountCurrency)
            VALUES
                (@order, @merchant, @orderNo, '77001', NULL, 1, 1, SYSUTCDATETIME(), NULL,
                 @token, DATEADD(day, 1, SYSUTCDATETIME()), NULL, NULL,
                 N'JSON Probe', '0800000000', NULL, 10.0000, 'THB');
            """,
            ("@merchant", merchantId), ("@code", $"json-{merchantId:N}"),
            ("@json", """{"source":"test"}"""), ("@cart", cartId), ("@order", orderId),
            ("@orderNo", $"ORD99{Random.Shared.Next(0, 99_999_999):D8}"),
            ("@token", $"json-{Guid.NewGuid():N}"));

        var validStatements = new[]
        {
            ("""
             INSERT admin.ProvisioningOperations
                 (Id, OperationKey, CallerAdminId, ExpectedAuthorizationVersion, RequestHash, MerchantId, Result, CreatedAt)
             VALUES (NEWID(), @key, NEWID(), 1, @hash, @merchant, @json, SYSUTCDATETIME());
             """, (string?)null),
            ("""
             INSERT merch.UserOutbox
                 (Id, MerchantId, Type, Payload, OccurredAt, ProcessedAt, Attempts, Error, LeaseExpiresAt, LeaseOwner)
             VALUES (NEWID(), @merchant, N'json.probe', @json, SYSUTCDATETIME(), NULL, 0, NULL, NULL, NULL);
             """, (string?)null),
            ("""
             INSERT shop.CartItems
                 (Id, CartId, MerchantId, ProductCode, SaleCode, VariantCode, VariantName, Quantity,
                  Metadata, UnitPriceAmount, UnitPriceCurrency)
             VALUES (NEWID(), @cart, @merchant, N'P-1', '77001', 'V-1', N'Variant', 1, @json, 10.0000, 'THB');
             """, (string?)null),
            ("""
             INSERT shop.OrderItems
                 (Id, OrderId, MerchantId, Quantity, ProductCode, VariantCode, VariantName, Metadata,
                  DiscountAmount, DiscountCurrency, UnitPriceAmount, UnitPriceCurrency)
             VALUES (NEWID(), @order, @merchant, 1, N'P-1', 'V-1', N'Variant', @json,
                     0.0000, 'THB', 10.0000, 'THB');
             """, (string?)null),
        };

        foreach (var (sql, _) in validStatements)
            await ExecuteAsync(connection, transaction, sql,
                ("@merchant", merchantId), ("@cart", cartId), ("@order", orderId),
                ("@key", $"json-{Guid.NewGuid():N}"), ("@hash", new string('a', 64)),
                ("@json", """{"source":"test"}"""));

        var invalidStatements = new[]
        {
            """
            INSERT merch.Merchants (Id, Code, Name, Note, Status, Country, Currency, EnabledChannels, CreatedAt, Metadata)
            VALUES (NEWID(), @key, N'bad', NULL, 1, N'TH', N'THB', N'card', SYSUTCDATETIME(), @json);
            """,
            validStatements[0].Item1,
            validStatements[1].Item1,
            validStatements[2].Item1,
            validStatements[3].Item1,
        };

        foreach (var sql in invalidStatements)
            await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(connection, transaction, sql,
                ("@merchant", merchantId), ("@cart", cartId), ("@order", orderId),
                ("@key", $"bad-{Guid.NewGuid():N}"), ("@hash", new string('b', 64)),
                ("@json", "{not-json")));

        await transaction.RollbackAsync();
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FreshJsonDatabase(string name, SqlConnection connection) : IAsyncDisposable
    {
        public SqlConnection Connection { get; } = connection;

        public static async Task<FreshJsonDatabase> CreateAsync()
        {
            var name = $"pol_json_{Guid.NewGuid():N}";
            await using (var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn))
            {
                await IntegrationDb.ExecAsync(master,
                    $"EXEC(N'CREATE DATABASE [{name}] COLLATE Thai_100_CI_AS');");
                await IntegrationDb.ExecAsync(master,
                    $"ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = 170;");
            }

            await using (var bootstrap = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(name)))
                await IntegrationDb.ExecAsync(bootstrap, "CREATE USER pol_app WITHOUT LOGIN;");

            await using (var context = CreateContext(name))
                await context.GetService<IMigrator>().MigrateAsync();

            return new FreshJsonDatabase(name, await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(name)));
        }

        private static PolDbContext CreateContext(string database)
        {
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170))
                .Options;
            return new PolDbContext(options, CurrentModuleAssemblies());
        }

        private static ModuleAssemblies CurrentModuleAssemblies() => new([
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
        ]);

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
            await IntegrationDb.ExecAsync(master,
                $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
        }
    }
}
