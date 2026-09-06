using BuildingBlocks.Infrastructure.Persistence;
using Governance.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class FreshBaselineMigrationIntegrationTests
{
    [Fact]
    public async Task Fresh_baseline_applies_and_rolls_back_in_dependency_safe_order()
    {
        var database = $"pol_baseline_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await CreateRuntimePrincipalAsync(database);
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync();

            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(context.Database.GetMigrations().Count(), Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    connection, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory;")));
                Assert.Equal("json", await IntegrationDb.ScalarAsync(connection, """
                    SELECT ty.name FROM sys.columns c
                    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                    WHERE c.object_id = OBJECT_ID(N'shop.CartItems') AND c.name = N'Metadata';
                    """));
                Assert.NotNull(await IntegrationDb.ScalarAsync(
                    connection, "SELECT OBJECT_ID(N'merch.RegistrationNotices', N'U');"));
                Assert.NotNull(await IntegrationDb.ScalarAsync(
                    connection, "SELECT OBJECT_ID(N'shop.OrderNoSeq', N'SO');"));
                Assert.NotNull(await IntegrationDb.ScalarAsync(
                    connection, "SELECT OBJECT_ID(N'admin.WorkforceTenantBindings', N'U');"));
                Assert.NotNull(await IntegrationDb.ScalarAsync(connection, """
                    SELECT object_id FROM sys.check_constraints
                    WHERE name = N'CK_WorkforceTenantBindings_Singleton';
                    """));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                    connection, "SELECT COUNT(*) FROM admin.WorkforceTenantBindings;")));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    SELECT Status FROM merch.Merchants
                    WHERE Id = 'e1000000-0000-4000-8000-000000000001';
                    """)));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    SELECT Psp FROM txn.PspConnections
                    WHERE Id = 'e8000000-0000-4000-8000-000000000001';
                    """)));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    SELECT is_nullable FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'merch.Users') AND name = N'IdentityType';
                    """)));
                Assert.Contains("[Status]", Convert.ToString(await IntegrationDb.ScalarAsync(connection, """
                    SELECT filter_definition FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'txn.PaymentSessions')
                      AND name = N'IX_PaymentSessions_OrderId_Open';
                    """)));
                Assert.DoesNotContain("0", Convert.ToString(await IntegrationDb.ScalarAsync(connection, """
                    SELECT filter_definition FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'txn.PaymentSessions')
                      AND name = N'IX_PaymentSessions_OrderId_Open';
                    """)));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT HAS_PERMS_BY_NAME(N'txn.AdminOperationRecords', N'OBJECT', N'UPDATE');
                    REVERT;
                    """)));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT HAS_PERMS_BY_NAME(N'admin.WorkforceTenantBindings', N'OBJECT', N'SELECT');
                    REVERT;
                    """)));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT HAS_PERMS_BY_NAME(N'admin.WorkforceTenantBindings', N'OBJECT', N'INSERT');
                    REVERT;
                    """)));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT HAS_PERMS_BY_NAME(N'admin.WorkforceTenantBindings', N'OBJECT', N'UPDATE');
                    REVERT;
                    """)));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT COUNT(*)
                    FROM (VALUES
                        (N'iam.ApiClients', N'SELECT'), (N'iam.ApiClients', N'INSERT'), (N'iam.ApiClients', N'UPDATE'),
                        (N'iam.OneTimeSecretTickets', N'SELECT'), (N'iam.OneTimeSecretTickets', N'INSERT'), (N'iam.OneTimeSecretTickets', N'UPDATE'),
                        (N'admin.DeliverySecretVersions', N'SELECT'), (N'admin.DeliverySecretVersions', N'INSERT'), (N'admin.DeliverySecretVersions', N'UPDATE'),
                        (N'txn.InboundWebhookEvents', N'SELECT'), (N'txn.InboundWebhookEvents', N'INSERT'), (N'txn.InboundWebhookEvents', N'UPDATE'),
                        (N'admin.NotificationDeliveries', N'SELECT'), (N'admin.NotificationDeliveries', N'INSERT'),
                        (N'admin.NotificationRules', N'SELECT'), (N'admin.NotificationRules', N'INSERT'), (N'admin.NotificationRules', N'UPDATE'), (N'admin.NotificationRules', N'DELETE'),
                        (N'admin.WebhookDeliveries', N'SELECT'), (N'admin.WebhookDeliveries', N'INSERT'), (N'admin.WebhookDeliveries', N'UPDATE'),
                        (N'admin.WebhookEndpoints', N'SELECT'), (N'admin.WebhookEndpoints', N'INSERT'), (N'admin.WebhookEndpoints', N'UPDATE'), (N'admin.WebhookEndpoints', N'DELETE')
                    ) required(ObjectName, PermissionName)
                    WHERE HAS_PERMS_BY_NAME(ObjectName, N'OBJECT', PermissionName) <> 1;
                    REVERT;
                    """)));
            }

            await migrator.MigrateAsync("0");

            await using var rolledBack = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(rolledBack, """
                SELECT COUNT(*) FROM sys.schemas
                WHERE name IN (N'admin', N'cfg', N'iam', N'merch', N'shop', N'txn');
                """)));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                rolledBack, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory;")));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Audit_hash_survives_sql_roundtrip_and_runtime_principal_cannot_rewrite_history()
    {
        var database = $"pol_audit_floor_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await CreateRuntimePrincipalAsync(database);
            await using var context = CreateContext(database);
            await context.Database.MigrateAsync();

            var now = new DateTime(2026, 8, 19, 12, 0, 0, DateTimeKind.Utc);
            var audit = AuditRecord.Append(
                "platform", GovernanceScopeKind.Platform, null, 1, AuditRecord.Genesis, Guid.NewGuid(),
                "admin.test.audit", "admin", Guid.NewGuid().ToString("D"),
                "succeeded", "{}", null, "v2", "corr-sql-roundtrip", now);

            await using (var insert = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await IntegrationDb.ExecAsync(insert, """
                    EXECUTE AS USER = 'pol_app';
                    INSERT admin.AuditHeads
                        (ScopeKey, ScopeKind, MerchantId, LastSequence, LastHash, UpdatedAt)
                    VALUES
                        (@scope, 1, NULL, 1, @hash, @occurredAt);
                    INSERT admin.AuditRecords
                        (Id, ScopeKey, ScopeKind, MerchantId, Sequence, ActorId, Action, ResourceType,
                         ResourceId, Result, Changes, ApprovalId, ResourceVersion, CorrelationId,
                         OccurredAt, PreviousHash, Hash)
                    VALUES
                        (@id, @scope, 1, NULL, 1, @actor, @action, @resourceType,
                         @resourceId, @result, @changes, NULL, @resourceVersion, @correlationId,
                         @occurredAt, @previousHash, @hash);
                    REVERT;
                    """,
                    ("@scope", audit.ScopeKey), ("@hash", audit.Hash), ("@occurredAt", audit.OccurredAt),
                    ("@id", audit.Id), ("@actor", audit.ActorId), ("@action", audit.Action),
                    ("@resourceType", audit.ResourceType), ("@resourceId", audit.ResourceId),
                    ("@result", audit.Result), ("@changes", audit.Changes),
                    ("@resourceVersion", audit.ResourceVersion!), ("@correlationId", audit.CorrelationId),
                    ("@previousHash", audit.PreviousHash));

            context.ChangeTracker.Clear();
            var loaded = await context.Set<AuditRecord>().AsNoTracking().SingleAsync(x => x.Id == audit.Id);
            var head = await context.Set<AuditHead>().AsNoTracking().SingleAsync(x => x.Id == "platform");
            Assert.Equal(DateTimeKind.Unspecified, loaded.OccurredAt.Kind);
            Assert.True(loaded.HasValidHash());
            Assert.Equal(loaded.Sequence, head.LastSequence);
            Assert.Equal(loaded.Hash, head.LastHash);

            await using (var update = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(update, """
                    EXECUTE AS USER = 'pol_app';
                    UPDATE admin.AuditRecords SET Result = N'tampered' WHERE Id = @id;
                    REVERT;
                    """, ("@id", audit.Id)));

            await using (var delete = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(delete, """
                    EXECUTE AS USER = 'pol_app';
                    DELETE FROM admin.AuditRecords WHERE Id = @id;
                    REVERT;
                    """, ("@id", audit.Id)));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Non_empty_target_is_refused_before_application_ddl()
    {
        var database = $"pol_refusal_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await CreateRuntimePrincipalAsync(database);
            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await IntegrationDb.ExecAsync(connection, "CREATE TABLE dbo.LegacyResidue (Id int NOT NULL);");

            await using var context = CreateContext(database);
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => context.GetService<IMigrator>().MigrateAsync());

            Assert.Contains("refused non-empty or legacy target database", error.ToString(), StringComparison.Ordinal);

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.NotNull(await IntegrationDb.ScalarAsync(
                verify, "SELECT OBJECT_ID(N'dbo.LegacyResidue', N'U');"));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM sys.schemas
                WHERE name IN (N'admin', N'cfg', N'iam', N'merch', N'shop', N'txn');
                """)));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Legacy_migration_history_is_refused_before_application_ddl()
    {
        var database = $"pol_history_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await CreateRuntimePrincipalAsync(database);
            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await IntegrationDb.ExecAsync(connection, """
                    CREATE TABLE dbo.__EFMigrationsHistory (
                        MigrationId nvarchar(150) NOT NULL CONSTRAINT PK___EFMigrationsHistory PRIMARY KEY,
                        ProductVersion nvarchar(32) NOT NULL
                    );
                    INSERT dbo.__EFMigrationsHistory (MigrationId, ProductVersion)
                    VALUES (N'20200101000000_Legacy', N'9.0.0');
                    """);

            await using var context = CreateContext(database);
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => context.GetService<IMigrator>().MigrateAsync());

            Assert.Contains("refused non-empty or legacy target database", error.ToString(), StringComparison.Ordinal);

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM sys.schemas
                WHERE name IN (N'admin', N'cfg', N'iam', N'merch', N'shop', N'txn');
                """)));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM dbo.__EFMigrationsHistory;")));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    private static PolDbContext CreateContext(string database)
    {
        var options = new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new PolDbContext(options, CurrentModuleAssemblies());
    }

    private static ModuleAssemblies CurrentModuleAssemblies() => new([
        typeof(Products.Infrastructure.ProductsModuleRegistration).Assembly,
        typeof(Carts.Infrastructure.CartModuleRegistration).Assembly,
        typeof(Orders.Infrastructure.OrdersModuleRegistration).Assembly,
        typeof(Payments.Infrastructure.PaymentsModuleRegistration).Assembly,
        typeof(Merchants.Infrastructure.MerchantsModuleRegistration).Assembly,
        typeof(Admins.Infrastructure.AdminModuleRegistration).Assembly,
        typeof(Iam.Infrastructure.IamModuleRegistration).Assembly,
        typeof(Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
        typeof(Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
    ]);

    private static async Task CreateScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master,
            $"EXEC(N'CREATE DATABASE [{database}] COLLATE Thai_100_CI_AS');");
        await IntegrationDb.ExecAsync(master,
            $"ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = 170;");
    }

    private static async Task CreateRuntimePrincipalAsync(string database)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        await IntegrationDb.ExecAsync(connection, "CREATE USER pol_app WITHOUT LOGIN;");
    }

    private static async Task DropScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }
}
