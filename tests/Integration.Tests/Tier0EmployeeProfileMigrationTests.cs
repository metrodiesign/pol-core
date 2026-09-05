using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integration.Tests;

/// <summary>
/// tier0-graph-employee-profile task 1 (REQ-8): the Tier0EmployeeProfile migration against a real scratch database.
/// Up adds the nullable profile columns + the filtered unique index, grants SELECT on the HR mirror tables ONLY when
/// they exist, and never creates them; Down removes exactly what Up added and leaves dbo.VibEmp / dbo.branch
/// untouched. The org reference lists that this migration also touched (cfg.Offices / cfg.Divisions LegacyKey) were
/// retired by DropOrgReferenceMasterData, so the assertions below stop at the columns that still exist at HEAD.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Tier0EmployeeProfileMigrationTests
{
    private const string PreviousMigration = "20260823132337_Tier0WorkforceEmailIdentity";

    [Fact]
    public async Task Up_adds_profile_columns_filtered_unique_indexes_and_keeps_guid_fks()
    {
        var database = $"pol_t0profile_up_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            await context.GetService<IMigrator>().MigrateAsync();

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            foreach (var (table, column, length) in new[]
                     {
                         ("admin.Users", "EmployeeId", 16), ("admin.Users", "FirstName", 500),
                         ("admin.Users", "LastName", 500),
                     })
            {
                // nvarchar max_length is bytes (2 per char); is_nullable must be 1 (REQ-8.1-8.3, 8.13, 6.1).
                Assert.Equal($"{length * 2}|1|nvarchar", Convert.ToString(await IntegrationDb.ScalarAsync(verify, $"""
                    SELECT CONCAT(c.max_length, '|', c.is_nullable, '|', t.name)
                    FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
                    WHERE c.object_id = OBJECT_ID(N'{table}') AND c.name = N'{column}';
                    """)));
            }

            foreach (var (table, index, filter) in new[]
                     {
                         ("admin.Users", "IX_Users_EmployeeId", "[EmployeeId] IS NOT NULL"),
                     })
            {
                // REQ-2.11 / 6.2 / 8.4: unique + filtered on non-NULL (SQL Server stores the filter parenthesised).
                var definition = Convert.ToString(await IntegrationDb.ScalarAsync(verify, $"""
                    SELECT CONCAT(is_unique, '|', filter_definition) FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{index}';
                    """));
                Assert.StartsWith("1|", definition, StringComparison.Ordinal);
                Assert.Contains(filter, definition, StringComparison.Ordinal);
            }

            // DropOrgReferenceMasterData: the org reference lists and the admin FK columns that pointed at them
            // are gone at HEAD — org data is read from the HR mirror instead.
            foreach (var table in new[] { "cfg.Positions", "cfg.Offices", "cfg.Levels", "cfg.Divisions" })
                Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(verify, $"SELECT OBJECT_ID(N'{table}', N'U');"));
            foreach (var column in new[] { "PositionId", "OfficeId", "LevelId", "DivisionId" })
                Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(verify, $"""
                    SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'admin.Users') AND name = N'{column}';
                    """) ?? DBNull.Value);

            // REQ-8.7 / 8.12: the migration neither creates the HR mirror tables nor fails without them.
            Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(verify, "SELECT OBJECT_ID(N'dbo.VibEmp', N'U');"));
            Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(verify, "SELECT OBJECT_ID(N'dbo.branch', N'U');"));

            // REQ-8.8: no backfill.
            Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.Users WHERE EmployeeId IS NOT NULL OR FirstName IS NOT NULL OR LastName IS NOT NULL;")));

            // REQ-2.11: duplicate non-NULL EmployeeId is rejected, NULLs are free.
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            await IntegrationDb.ExecAsync(verify, $"""
                INSERT admin.Users (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt, EmployeeId)
                VALUES ('{a}', N'microsoft', N'a', N'a@example.com', 1, 1, 0, 1, SYSUTCDATETIME(), N'ZTEST1'),
                       ('{b}', N'microsoft', N'b', N'b@example.com', 1, 1, 0, 1, SYSUTCDATETIME(), NULL),
                       ('{Guid.NewGuid()}', N'microsoft', N'c', N'c@example.com', 1, 1, 0, 1, SYSUTCDATETIME(), NULL);
                """);
            var dup = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => IntegrationDb.ExecAsync(verify,
                $"UPDATE admin.Users SET EmployeeId = N'ZTEST1' WHERE Id = '{b}';"));
            Assert.Equal(2601, dup.Number);
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Up_grants_pol_app_select_on_hr_tables_only_when_they_exist_and_down_leaves_them_alone()
    {
        var database = $"pol_t0profile_grant_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            // Operator-loaded mirror tables exist BEFORE this migration (minimal shape; never created by any migration).
            await using (var setup = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await IntegrationDb.ExecAsync(setup, """
                    CREATE TABLE dbo.VibEmp (EmpCode nvarchar(50) NULL, FirstNameTh nvarchar(500) NULL,
                        LastNameTh nvarchar(500) NULL, und_brcode char(3) NULL, DepartmentID nvarchar(50) NULL);
                    CREATE TABLE dbo.branch (br_code char(3) NULL);
                    INSERT dbo.VibEmp (EmpCode) VALUES (N'ZTEST-KEEP');
                    """);

            await migrator.MigrateAsync();

            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                // REQ-8.11: explicit SELECT grant to pol_app on both tables.
                Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT COUNT(*) FROM sys.database_permissions p
                    JOIN sys.database_principals u ON u.principal_id = p.grantee_principal_id
                    WHERE u.name = N'pol_app' AND p.permission_name = N'SELECT' AND p.state = 'G'
                      AND p.major_id IN (OBJECT_ID(N'dbo.VibEmp'), OBJECT_ID(N'dbo.branch'));
                    """)));
                // REQ-8.7: the mirror data is untouched by Up.
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify,
                    "SELECT COUNT(*) FROM dbo.VibEmp WHERE EmpCode = N'ZTEST-KEEP';")));
            }

            await migrator.MigrateAsync(PreviousMigration); // Down (REQ-8.9)

            await using (var down = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                foreach (var (table, column) in new[]
                         {
                             ("admin.Users", "EmployeeId"), ("admin.Users", "FirstName"), ("admin.Users", "LastName"),
                         })
                    Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(down, $"""
                        SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'{table}') AND name = N'{column}';
                        """) ?? DBNull.Value);
                Assert.NotEqual(DBNull.Value, await IntegrationDb.ScalarAsync(down, "SELECT OBJECT_ID(N'dbo.VibEmp', N'U');"));
                Assert.NotEqual(DBNull.Value, await IntegrationDb.ScalarAsync(down, "SELECT OBJECT_ID(N'dbo.branch', N'U');"));
                Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(down,
                    "SELECT COUNT(*) FROM dbo.VibEmp WHERE EmpCode = N'ZTEST-KEEP';")));
                // Rolling back past DropOrgReferenceMasterData recreates the retired tables EMPTY — the drop is a
                // one-way data decision, its Down only restores the shape.
                Assert.NotEqual(DBNull.Value, await IntegrationDb.ScalarAsync(down, "SELECT OBJECT_ID(N'cfg.Offices', N'U');"));
                Assert.NotEqual(DBNull.Value, await IntegrationDb.ScalarAsync(down, "SELECT OBJECT_ID(N'cfg.Divisions', N'U');"));
            }

            await migrator.MigrateAsync(); // Up again round-trips
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
