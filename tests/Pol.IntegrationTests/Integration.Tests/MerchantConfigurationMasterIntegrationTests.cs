using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Payments.Domain.Capabilities;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "MerchantConfiguration")]
public sealed class MerchantConfigurationMasterIntegrationTests
{
    [Fact]
    [Trait("Requirement", "REQ-5.1")]
    [Trait("Requirement", "REQ-5.2")]
    public async Task Sql_server_keeps_master_codes_merchant_scoped_and_rejects_cross_merchant_sale_branch_and_provider_refs()
    {
        var database = $"pol_merchant_config_{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            await context.GetService<IMigrator>().MigrateAsync();
            await using var db = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));

            var merchantA = Guid.NewGuid();
            var merchantB = Guid.NewGuid();
            var branchA = Guid.NewGuid();
            var branchB = Guid.NewGuid();
            await IntegrationDb.InsertMerchantAsync(db, merchantA, $"mc-a-{Guid.NewGuid():N}"[..24]);
            await IntegrationDb.InsertMerchantAsync(db, merchantB, $"mc-b-{Guid.NewGuid():N}"[..24]);

            await InsertBranchAsync(db, branchA, merchantA, "central");
            await InsertBranchAsync(db, branchB, merchantB, "central");
            await AssertSqlConstraintAsync(() => InsertBranchAsync(db, Guid.NewGuid(), merchantA, "central"));

            await InsertSaleAsync(db, Guid.NewGuid(), merchantA, branchA, "agent-01");
            await InsertSaleAsync(db, Guid.NewGuid(), merchantB, branchB, "agent-01");
            await AssertSqlConstraintAsync(() => InsertSaleAsync(
                db, Guid.NewGuid(), merchantA, branchB, "foreign-branch"));
            await AssertSqlConstraintAsync(() => InsertSaleAsync(
                db, Guid.NewGuid(), merchantA, branchA, "agent-01"));

            var connectionA = Guid.NewGuid();
            var connectionB = Guid.NewGuid();
            await InsertConnectionAsync(db, connectionA, merchantA);
            await InsertConnectionAsync(db, connectionB, merchantB);

            // ProviderAccount method rows carry MerchantId and PspConnectionId together. The composite FK
            // rejects a caller that tries to bind Merchant A's method policy to Merchant B's account.
            await AssertSqlConstraintAsync(() => InsertProviderAccountMethodAsync(
                db, merchantA, connectionB, Guid.NewGuid()));

            // The routing writer has the same composite owner guard for a direct PspConnection reference.
            var rulesetId = Guid.NewGuid();
            await IntegrationDb.ExecAsync(db, $"""
                INSERT txn.RoutingRulesets
                    (Id, MerchantId, Name, Status, ApprovalId, CreatedAt, UpdatedAt, Version)
                VALUES ('{rulesetId}', '{merchantA}', N'merchant-a', 1, NULL,
                        SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                """);
            await AssertSqlConstraintAsync(() => IntegrationDb.ExecAsync(db, $"""
                INSERT txn.RoutingRules
                    (Id, MerchantId, RulesetId, Priority, Method, OriginatorId,
                     MinAmount, MaxAmount, TargetConnectionId, FallbackConnectionId, Enabled)
                VALUES ('{Guid.NewGuid()}', '{merchantA}', '{rulesetId}', 1, N'card', NULL,
                        NULL, NULL, '{connectionB}', NULL, 1);
                """));
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static PolDbContext CreateContext(string database)
    {
        var options = new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new PolDbContext(options, new ModuleAssemblies([
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
    }

    private static Task InsertBranchAsync(SqlConnection db, Guid id, Guid merchantId, string code) =>
        IntegrationDb.ExecAsync(db, """
            INSERT merch.Branches
                (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
            VALUES (@id, @merchant, @code, N'Branch', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
            """,
            ("@id", id), ("@merchant", merchantId), ("@code", code));

    private static Task InsertSaleAsync(
        SqlConnection db, Guid id, Guid merchantId, Guid branchId, string code) =>
        IntegrationDb.ExecAsync(db, """
            INSERT merch.Sales
                (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
            VALUES (@id, @merchant, @branch, @code, N'Sale', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
            """,
            ("@id", id), ("@merchant", merchantId), ("@branch", branchId), ("@code", code));

    private static Task InsertConnectionAsync(SqlConnection db, Guid id, Guid merchantId) =>
        IntegrationDb.ExecAsync(db, """
            INSERT txn.PspConnections
                (Id, MerchantId, Psp, PaymentProviderId, EnabledMethods, SecretRefName,
                 IsEnabled, CreatedAt, Health, Version)
            VALUES (@id, @merchant, 1, @provider, N'card', N'task3-test',
                    1, SYSUTCDATETIME(), 1, 1);
            """,
            ("@id", id), ("@merchant", merchantId), ("@provider", PaymentCapabilityIds.TwoCTwoP));

    private static Task InsertProviderAccountMethodAsync(
        SqlConnection db, Guid merchantId, Guid connectionId, Guid id) =>
        IntegrationDb.ExecAsync(db, """
            INSERT txn.MerchantProviderAccountMethods
                (Id, MerchantId, PspConnectionId, PaymentProviderId, PaymentProviderMethodId,
                 PaymentMethodId, IsEnabled, CreatedBy, CreatedAt, Version)
            VALUES (@id, @merchant, @connection, @provider, @providerMethod,
                    @method, 1, @actor, SYSUTCDATETIME(), 1);
            """,
            ("@id", id),
            ("@merchant", merchantId),
            ("@connection", connectionId),
            ("@provider", PaymentCapabilityIds.TwoCTwoP),
            ("@providerMethod", PaymentCapabilityIds.TwoCTwoPCard),
            ("@method", PaymentCapabilityIds.Card),
            ("@actor", Guid.NewGuid()));

    private static async Task AssertSqlConstraintAsync(Func<Task> write)
    {
        var error = await Assert.ThrowsAsync<SqlException>(write);
        Assert.Contains(error.Number, new[] { 547, 2601, 2627 });
    }
}
