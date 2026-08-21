using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integration.Tests;

[Trait("Category", "Integration")]
public sealed class WorkforceTenantBindingIntegrationTests
{
    private const string PreviousMigration = "20260817172338_MerchantPaymentCapabilityControlPlane";
    private const string ThisMigration = "20260819145219_WorkforceTenantBinding";
    private static readonly Guid TenantId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    [Fact]
    public async Task Migration_canonicalizes_valid_subject_and_creates_append_only_runtime_grants()
    {
        var database = $"pol_workforce_up_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            var microsoftAdminId = Guid.NewGuid();
            var googleAdminId = Guid.NewGuid();
            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await IntegrationDb.ExecAsync(seed, """
                    INSERT admin.Users
                        (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                    VALUES
                        (@microsoftId, N'microsoft', @microsoftSubject, N'microsoft@example.invalid', 1, 1, 0, 1, SYSUTCDATETIME()),
                        (@googleId, N'google', N'Google-Subject-Stays-As-Is', N'google@example.invalid', 1, 1, 0, 1, SYSUTCDATETIME());
                    """,
                    ("@microsoftId", microsoftAdminId), ("@microsoftSubject", TenantId.ToString("D").ToUpperInvariant()),
                    ("@googleId", googleAdminId));

            await migrator.MigrateAsync();

            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(TenantId.ToString("D"), Convert.ToString(await IntegrationDb.ScalarAsync(verify,
                    "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", microsoftAdminId))));
                Assert.Equal("Google-Subject-Stays-As-Is", Convert.ToString(await IntegrationDb.ScalarAsync(verify,
                    "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", googleAdminId))));
                Assert.NotNull(await IntegrationDb.ScalarAsync(verify,
                    "SELECT OBJECT_ID(N'admin.WorkforceTenantBindings', N'U');"));
                Assert.NotNull(await IntegrationDb.ScalarAsync(verify, """
                    SELECT object_id FROM sys.check_constraints
                    WHERE name = N'CK_WorkforceTenantBindings_Singleton';
                    """));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT HAS_PERMS_BY_NAME(N'admin.WorkforceTenantBindings', N'OBJECT', N'SELECT');
                    REVERT;
                    """)));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT HAS_PERMS_BY_NAME(N'admin.WorkforceTenantBindings', N'OBJECT', N'INSERT');
                    REVERT;
                    """)));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    EXECUTE AS USER = 'pol_app';
                    SELECT HAS_PERMS_BY_NAME(N'admin.WorkforceTenantBindings', N'OBJECT', N'UPDATE');
                    REVERT;
                    """)));

                await IntegrationDb.ExecAsync(verify, """
                    EXECUTE AS USER = 'pol_app';
                    INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (1, @tenantId);
                    REVERT;
                    """, ("@tenantId", TenantId));
            }

            await using (var update = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(update, """
                    EXECUTE AS USER = 'pol_app';
                    UPDATE admin.WorkforceTenantBindings SET TenantId = @tenantId WHERE Id = 1;
                    REVERT;
                    """, ("@tenantId", Guid.NewGuid())));

            await using (var delete = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(delete, """
                    EXECUTE AS USER = 'pol_app';
                    DELETE FROM admin.WorkforceTenantBindings WHERE Id = 1;
                    REVERT;
                    """));

            await migrator.MigrateAsync(PreviousMigration);
            await using (var down = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(down,
                    "SELECT OBJECT_ID(N'admin.WorkforceTenantBindings', N'U');"));
                Assert.Equal(TenantId.ToString("D"), Convert.ToString(await IntegrationDb.ScalarAsync(down,
                    "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", microsoftAdminId))));
            }

            await migrator.MigrateAsync(ThisMigration);
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Migration_rejects_every_non_exact_subject_without_echoing_identity()
    {
        var database = $"pol_workforce_invalid_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            var adminId = Guid.NewGuid();

            await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await IntegrationDb.ExecAsync(seed, """
                    INSERT admin.Users
                        (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                    VALUES (@id, N'microsoft', @subject, N'invalid@example.invalid', 1, 1, 0, 1, SYSUTCDATETIME());
                    """, ("@id", adminId), ("@subject", TenantId.ToString("D")));

            string[] invalidSubjects =
            [
                TenantId.ToString("D") + "suffix",
                TenantId.ToString("B"),
                TenantId.ToString("N"),
                TenantId.ToString("D") + " ",
                "not-a-guid",
                "zzzzzzzz-zzzz-zzzz-zzzz-zzzzzzzzzzzz",
            ];

            foreach (var invalidSubject in invalidSubjects)
            {
                await using var update = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
                await IntegrationDb.ExecAsync(update,
                    "UPDATE admin.Users SET Subject = @subject WHERE Id = @id;",
                    ("@subject", invalidSubject), ("@id", adminId));

                var error = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(ThisMigration));
                Assert.Contains("exact UUID D values", error.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(invalidSubject, error.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(update,
                    "SELECT OBJECT_ID(N'admin.WorkforceTenantBindings', N'U');"));
            }
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Migration_rejects_semantic_duplicate_guids_before_normalization()
    {
        var database = $"pol_workforce_duplicate_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            await using var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            await IntegrationDb.ExecAsync(seed, """
                DROP INDEX IX_Users_Provider_Subject ON admin.Users;
                INSERT admin.Users
                    (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                VALUES
                    (@id1, N'microsoft', @lower, N'duplicate-1@example.invalid', 1, 1, 0, 1, SYSUTCDATETIME()),
                    (@id2, N'microsoft', @upper, N'duplicate-2@example.invalid', 1, 1, 0, 1, SYSUTCDATETIME());
                """,
                ("@id1", Guid.NewGuid()), ("@lower", TenantId.ToString("D")),
                ("@id2", Guid.NewGuid()), ("@upper", TenantId.ToString("D").ToUpperInvariant()));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(ThisMigration));

            Assert.Contains("Duplicate Microsoft admin identities", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(TenantId.ToString("D"), error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(seed,
                "SELECT OBJECT_ID(N'admin.WorkforceTenantBindings', N'U');"));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Tenant_binding_applock_serializes_concurrent_first_initializers()
    {
        var database = $"pol_workforce_lock_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using (var context = CreateContext(database))
                await context.Database.MigrateAsync();

            const int initializerCount = 12;
            await Task.WhenAll(Enumerable.Range(0, initializerCount)
                .Select(_ => InitializeTenantBindingAsync(database, TenantId)));

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM admin.WorkforceTenantBindings;")));
            Assert.Equal(TenantId, await IntegrationDb.ScalarAsync(
                verify, "SELECT TenantId FROM admin.WorkforceTenantBindings WHERE Id = 1;"));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    private static async Task InitializeTenantBindingAsync(string database, Guid tenantId)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = """
                DECLARE @lock int;
                EXEC @lock = sp_getapplock
                    @Resource = N'admin-workforce-tenant-binding',
                    @LockMode = N'Exclusive',
                    @LockOwner = N'Transaction',
                    @LockTimeout = 15000;
                SELECT @lock AS Value;
                """;
            var lockResult = Convert.ToInt32(await lockCommand.ExecuteScalarAsync());
            if (lockResult < 0)
                throw new InvalidOperationException("Could not acquire the governance transaction lock.");
        }

        object? existing;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT TenantId FROM admin.WorkforceTenantBindings WHERE Id = 1;";
            existing = await read.ExecuteScalarAsync();
        }

        if (existing is null or DBNull)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (1, @tenantId);";
            insert.Parameters.AddWithValue("@tenantId", tenantId);
            await insert.ExecuteNonQueryAsync();
        }
        else if ((Guid)existing != tenantId)
        {
            throw new InvalidOperationException(
                "Admin Microsoft Authority does not match the persisted workforce tenant binding.");
        }

        await transaction.CommitAsync();
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
        typeof(Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
        typeof(Levels.Infrastructure.LevelsModuleRegistration).Assembly,
        typeof(Offices.Infrastructure.OfficesModuleRegistration).Assembly,
        typeof(Positions.Infrastructure.PositionsModuleRegistration).Assembly,
        typeof(Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
        typeof(Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
    ]);

    private static async Task CreateScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master, $"EXEC(N'CREATE DATABASE [{database}] COLLATE Thai_100_CI_AS');");
        await IntegrationDb.ExecAsync(master, $"ALTER DATABASE [{database}] SET COMPATIBILITY_LEVEL = 170;");
        await using var scratch = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
        await IntegrationDb.ExecAsync(scratch, "CREATE USER pol_app WITHOUT LOGIN;");
    }

    private static async Task DropScratchDatabaseAsync(string database)
    {
        await using var master = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(master,
            $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }
}
