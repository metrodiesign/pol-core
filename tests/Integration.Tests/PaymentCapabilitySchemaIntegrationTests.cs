using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Payments.Domain.Capabilities;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class PaymentCapabilitySchemaIntegrationTests
{
    [Fact]
    public async Task PaymentPolicyTenantIsolation_and_relational_guards_reject_invalid_identity_tenant_and_parent_chains()
    {
        var database = $"pol_capability_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            await context.GetService<IMigrator>().MigrateAsync();
            await using var db = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));

            Assert.Equal("card,installment,promptpay", Convert.ToString(await IntegrationDb.ScalarAsync(db, """
                SELECT STRING_AGG(Code, ',') WITHIN GROUP (ORDER BY Code) FROM cfg.PaymentMethods;
                """)));
            Assert.Equal("BAY,KBANK,KTC,SCB", Convert.ToString(await IntegrationDb.ScalarAsync(db, """
                SELECT STRING_AGG(Code, ',') WITHIN GROUP (ORDER BY Code) FROM cfg.PaymentMethodOptions;
                """)));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                db, "SELECT COUNT(*) FROM cfg.PaymentProviderMethodOptions;")));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(db, """
                SELECT COUNT(*)
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE CONCAT(s.name, '.', t.name) IN (
                    'cfg.PaymentMethods', 'cfg.PaymentMethodOptionGroups', 'cfg.PaymentMethodOptions',
                    'cfg.PaymentProviders', 'cfg.PaymentProviderMethods', 'cfg.PaymentProviderMethodOptions',
                    'txn.MerchantProviderAccountMethods', 'txn.MerchantProviderAccountMethodOptions',
                    'txn.MerchantPaymentMethods', 'txn.MerchantUserPaymentMethods')
                  AND (c.name = 'Id' OR c.name LIKE '%Id')
                  AND TYPE_NAME(c.user_type_id) <> 'uniqueidentifier';
                """)));
            Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(db, """
                SELECT COUNT(*) FROM sys.foreign_keys WHERE name IN (
                    'FK_MerchantUserPaymentMethods_Users_User_Merchant',
                    'FK_MerchantProviderAccountMethods_PspConnections_Account_Provider',
                    'FK_Orders_Users_Initiator_Merchant');
                """)));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(db, """
                SELECT COUNT(*) FROM sys.key_constraints WHERE name IN (
                    'UQ_Users_Id_MerchantId',
                    'UQ_PspConnections_Id_MerchantId_PaymentProviderId');
                """)));

            await AssertConstraintRejectedAsync(() => InsertUserAsync(db, Guid.NewGuid(), null, 2, "active-no-merchant"));

            var merchantA = Guid.NewGuid();
            var merchantB = Guid.NewGuid();
            var userA = Guid.NewGuid();
            var actor = Guid.NewGuid();
            await IntegrationDb.InsertMerchantAsync(db, merchantA, $"cap-a-{Guid.NewGuid():N}"[..24]);
            await IntegrationDb.InsertMerchantAsync(db, merchantB, $"cap-b-{Guid.NewGuid():N}"[..24]);
            await InsertUserAsync(db, userA, merchantA, 2, "user-a");
            await AssertConstraintRejectedAsync(() =>
                InsertUserAsync(db, Guid.NewGuid(), merchantB, 2, "user-a"));

            await InsertMerchantPolicyAsync(db, merchantB, PaymentCapabilityIds.Card, actor);
            await AssertConstraintRejectedAsync(() => IntegrationDb.ExecAsync(db, $"""
                INSERT txn.MerchantUserPaymentMethods
                    (Id, MerchantUserId, MerchantId, PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
                VALUES
                    ('{Guid.NewGuid()}', '{userA}', '{merchantB}', '{PaymentCapabilityIds.Card}', 1,
                     '{actor}', SYSUTCDATETIME(), 1);
                """));

            await InsertMerchantPolicyAsync(db, merchantA, PaymentCapabilityIds.Card, actor);
            await AssertConstraintRejectedAsync(() =>
                InsertMerchantPolicyAsync(db, merchantA, PaymentCapabilityIds.Card, actor));
            await IntegrationDb.ExecAsync(db, $"""
                INSERT txn.MerchantUserPaymentMethods
                    (Id, MerchantUserId, MerchantId, PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
                VALUES
                    ('{Guid.NewGuid()}', '{userA}', '{merchantA}', '{PaymentCapabilityIds.Card}', 1,
                     '{actor}', SYSUTCDATETIME(), 1);
                """);
            await AssertConstraintRejectedAsync(() => IntegrationDb.ExecAsync(db, $"""
                INSERT txn.MerchantUserPaymentMethods
                    (Id, MerchantUserId, MerchantId, PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
                VALUES
                    ('{Guid.NewGuid()}', '{userA}', '{merchantA}', '{PaymentCapabilityIds.Card}', 1,
                     '{actor}', SYSUTCDATETIME(), 1);
                """));

            var connectionId = Guid.NewGuid();
            await InsertConnectionAsync(db, connectionId, merchantA, PaymentCapabilityIds.TwoCTwoP, psp: 1);
            await AssertConstraintRejectedAsync(() => IntegrationDb.ExecAsync(db, $"""
                INSERT txn.MerchantProviderAccountMethods
                    (Id, MerchantId, PspConnectionId, PaymentProviderId, PaymentProviderMethodId,
                     PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
                VALUES
                    ('{Guid.NewGuid()}', '{merchantA}', '{connectionId}', '{PaymentCapabilityIds.Omise}',
                     '{PaymentCapabilityIds.OmiseCard}', '{PaymentCapabilityIds.Card}', 1,
                     '{actor}', SYSUTCDATETIME(), 1);
                """));

            var accountMethodId = Guid.NewGuid();
            await IntegrationDb.ExecAsync(db, $"""
                INSERT txn.MerchantProviderAccountMethods
                    (Id, MerchantId, PspConnectionId, PaymentProviderId, PaymentProviderMethodId,
                     PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
                VALUES
                    ('{accountMethodId}', '{merchantA}', '{connectionId}', '{PaymentCapabilityIds.TwoCTwoP}',
                     '{PaymentCapabilityIds.TwoCTwoPInstallment}', '{PaymentCapabilityIds.Installment}', 1,
                     '{actor}', SYSUTCDATETIME(), 1);
                """);
            var providerOptionId = Guid.NewGuid();
            await IntegrationDb.ExecAsync(db, $"""
                INSERT cfg.PaymentProviderMethodOptions
                    (Id, PaymentProviderMethodId, PaymentMethodId, PaymentMethodOptionId, IsActive,
                     CreatedBy, CreatedAt, Version)
                VALUES
                    ('{providerOptionId}', '{PaymentCapabilityIds.TwoCTwoPInstallment}',
                     '{PaymentCapabilityIds.Installment}', '{PaymentCapabilityIds.Kbank}', 1,
                     '{actor}', SYSUTCDATETIME(), 1);
                """);
            await AssertConstraintRejectedAsync(() => IntegrationDb.ExecAsync(db, $"""
                INSERT txn.MerchantProviderAccountMethodOptions
                    (Id, MerchantId, MerchantProviderAccountMethodId, PspConnectionId, PaymentProviderId,
                     PaymentProviderMethodId, PaymentMethodId, PaymentProviderMethodOptionId,
                     PaymentMethodOptionId, IsEnabled, CreatedBy, CreatedAt, Version)
                VALUES
                    ('{Guid.NewGuid()}', '{merchantA}', '{accountMethodId}', '{connectionId}',
                     '{PaymentCapabilityIds.TwoCTwoP}', '{PaymentCapabilityIds.TwoCTwoPInstallment}',
                     '{PaymentCapabilityIds.Installment}', '{providerOptionId}', '{PaymentCapabilityIds.Scb}',
                     1, '{actor}', SYSUTCDATETIME(), 1);
                """));

            await AssertConstraintRejectedAsync(() => InsertConnectionAsync(
                db, Guid.NewGuid(), merchantA, PaymentCapabilityIds.Omise, psp: 1));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    private static Task InsertUserAsync(
        SqlConnection db, Guid id, Guid? merchantId, int status, string subject) =>
        IntegrationDb.ExecAsync(db, """
            INSERT merch.Users
                (Id, Provider, Subject, Email, Status, MerchantId, Version, CreatedAt,
                 DisplayName, FirstName, LastName, IdentityType)
            VALUES
                (@id, N'google', @subject, N'user@example.com', @status, @merchant, 1,
                 SYSUTCDATETIME(), N'User One', N'User', N'One', 1);
            """,
            ("@id", id), ("@subject", subject), ("@status", status),
            ("@merchant", (object?)merchantId ?? DBNull.Value));

    private static Task InsertMerchantPolicyAsync(
        SqlConnection db, Guid merchantId, Guid methodId, Guid actorId) =>
        IntegrationDb.ExecAsync(db, $"""
            INSERT txn.MerchantPaymentMethods
                (Id, MerchantId, PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
            VALUES
                ('{Guid.NewGuid()}', '{merchantId}', '{methodId}', 1, '{actorId}', SYSUTCDATETIME(), 1);
            """);

    private static Task InsertConnectionAsync(
        SqlConnection db, Guid id, Guid merchantId, Guid providerId, int psp) =>
        IntegrationDb.ExecAsync(db, $"""
            INSERT txn.PspConnections
                (Id, MerchantId, Psp, PaymentProviderId, EnabledMethods, SecretRefName,
                 IsEnabled, CreatedAt, Health, Version)
            VALUES
                ('{id}', '{merchantId}', {psp}, '{providerId}', N'card', N'capability-test',
                 1, SYSUTCDATETIME(), 1, 1);
            """);

    private static async Task AssertConstraintRejectedAsync(Func<Task> write)
    {
        var error = await Assert.ThrowsAsync<SqlException>(write);
        Assert.Contains(error.Number, new[] { 547, 2601, 2627 });
    }

    internal static PolDbContext CreateContext(string database)
    {
        var options = new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new PolDbContext(options, new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
            typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
            typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
            typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
            typeof(Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
            typeof(Admins.Infrastructure.AdminModuleRegistration).Assembly,
            typeof(Iam.Infrastructure.IamModuleRegistration).Assembly,
            typeof(Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
            typeof(Levels.Infrastructure.LevelsModuleRegistration).Assembly,
            typeof(Offices.Infrastructure.OfficesModuleRegistration).Assembly,
            typeof(Positions.Infrastructure.PositionsModuleRegistration).Assembly,
            typeof(Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
        ]));
    }

    internal static async Task CreateScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master, $"EXEC(N'CREATE DATABASE [{database}] COLLATE Thai_100_CI_AS');");
        await IntegrationDb.ExecAsync(master, $"ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = 170;");
        await using var scratch = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        await IntegrationDb.ExecAsync(scratch, "CREATE USER pol_app WITHOUT LOGIN;");
    }

    internal static async Task DropScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }
}
