using BuildingBlocks.Infrastructure.Persistence;
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
                Assert.Equal(3, Convert.ToInt32(await IntegrationDb.ScalarAsync(
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
        typeof(Divisions.Infrastructure.DivisionsModuleRegistration).Assembly,
        typeof(Levels.Infrastructure.LevelsModuleRegistration).Assembly,
        typeof(Offices.Infrastructure.OfficesModuleRegistration).Assembly,
        typeof(Positions.Infrastructure.PositionsModuleRegistration).Assembly,
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
