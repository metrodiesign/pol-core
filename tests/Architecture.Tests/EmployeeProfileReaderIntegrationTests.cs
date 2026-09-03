using Admins.Application.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;

namespace Architecture.Tests;

/// <summary>
/// tier0-graph-employee-profile task 2: <see cref="EmployeeProfileReader"/> against a REAL scratch database, reading
/// as the REAL <c>pol_app</c> login (the server-level login the dev database already has). The HR mirror tables are
/// created with the minimal REQ-11.7 shape before the migration so the conditional GRANT (REQ-8.11) applies; rows use
/// the <c>ZTEST-</c> prefix only and the database is dropped afterwards — no shared-database PII is ever read.
/// Lives here (not Integration.Tests) because <c>ControlPlaneDbContext</c> is internal to Persistence.ControlPlane.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EmployeeProfileReaderIntegrationTests
{
    private static readonly Guid Hq = Guid.Parse("b2000000-0000-4000-8000-000000000001");
    private static readonly Guid North = Guid.Parse("b2000000-0000-4000-8000-000000000002");
    private static readonly Guid Finance = Guid.Parse("d4000000-0000-4000-8000-000000000002");
    private static readonly Guid Legal = Guid.Parse("d4000000-0000-4000-8000-000000000008");

    [Fact]
    public async Task Reads_every_status_of_the_design_table_as_pol_app()
    {
        await using var database = await ScratchDatabase.CreateAsync(createHrTables: true);
        await database.ExecuteAsync("""
            INSERT dbo.VibEmp (EmpCode, FirstNameTh, LastNameTh, und_brcode, DepartmentID) VALUES
                (N'ZTEST-OK',      N' สมชาย ',  N' ใจดี ', 'Z01', N'ZD1'),
                (N'ZTEST-DUP',     N'a', N'b', 'Z01', N'ZD1'),
                (N'ZTEST-DUP',     N'c', N'd', 'Z01', N'ZD1'),
                (N'ZTEST-NONAME',  N'   ', N'x', 'Z01', N'ZD1'),
                (N'ZTEST-LONG',    REPLICATE(N'ก', 501), N'x', 'Z01', N'ZD1'),
                (N'ZTEST-NOBR',    N'a', N'b', NULL, N'ZD1'),
                (N'ZTEST-BADBR',   N'a', N'b', 'Z99', N'ZD1'),
                (N'ZTEST-NOOFF',   N'a', N'b', 'Z02', N'ZD1'),
                (N'ZTEST-NODEPT',  N'a', N'b', 'Z01', N''),
                (N'ZTEST-NODIV',   N'a', N'b', 'Z01', N'ZD9'),
                (N'ZTEST-INACT',   N'a', N'b', 'Z03', N'ZD3');
            INSERT dbo.branch (br_code, active_row) VALUES ('Z01', 1), ('Z02', 0), ('Z03', 1);
            UPDATE cfg.Offices SET LegacyKey = N'Z01' WHERE Id = @hq;
            UPDATE cfg.Offices SET LegacyKey = N'Z03', Status = 2 WHERE Id = @north;
            UPDATE cfg.Divisions SET LegacyKey = N'ZD1' WHERE Id = @finance;
            UPDATE cfg.Divisions SET LegacyKey = N'ZD3', Status = 2 WHERE Id = @legal;
            """, ("@hq", Hq), ("@north", North), ("@finance", Finance), ("@legal", Legal));

        await using var context = database.AppContext();
        var reader = new EmployeeProfileReader(context, new CapturingLogger());

        var found = await reader.LookupAsync("ZTEST-OK", CancellationToken.None);
        Assert.Equal(EmployeeProfileStatus.Found, found.Status);
        // REQ-3.6/3.7 trimmed Thai names, REQ-4.7/5.3 GUIDs via LegacyKey, active flags surfaced (REQ-4.11/5.7 inputs).
        Assert.Equal(new EmployeeProfile("สมชาย", "ใจดี", Hq, true, Finance, true), found.Profile);

        // REQ-3.2 / 4.4 / 6.7: trailing whitespace on the lookup key and char(3) padding do not matter.
        Assert.Equal(EmployeeProfileStatus.Found, (await reader.LookupAsync("ZTEST-OK ", CancellationToken.None)).Status);
        // REQ-3.3: no prefix / pattern match.
        Assert.Equal(EmployeeProfileStatus.Missing, (await reader.LookupAsync("ZTEST-O", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Missing, (await reader.LookupAsync("ZTEST-%", CancellationToken.None)).Status);

        Assert.Equal(EmployeeProfileStatus.Missing, (await reader.LookupAsync("ZTEST-NONE", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Invalid, (await reader.LookupAsync("ZTEST-DUP", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Invalid, (await reader.LookupAsync("ZTEST-NONAME", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Invalid, (await reader.LookupAsync("ZTEST-LONG", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Unmapped, (await reader.LookupAsync("ZTEST-NOBR", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Unmapped, (await reader.LookupAsync("ZTEST-BADBR", CancellationToken.None)).Status);
        // REQ-4.18: dbo.branch.active_row = 0 is still a valid branch; the office mapping is what is missing here.
        Assert.Equal(EmployeeProfileStatus.Unmapped, (await reader.LookupAsync("ZTEST-NOOFF", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Unmapped, (await reader.LookupAsync("ZTEST-NODEPT", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Unmapped, (await reader.LookupAsync("ZTEST-NODIV", CancellationToken.None)).Status);

        var inactive = await reader.LookupAsync("ZTEST-INACT", CancellationToken.None);
        Assert.Equal(EmployeeProfileStatus.Found, inactive.Status);
        Assert.Equal(new EmployeeProfile("a", "b", North, false, Legal, false), inactive.Profile);

        // REQ-3.11 / 4.15: pol_app holds SELECT only on the mirror tables.
        await using var verify = await database.OpenAsync();
        foreach (var table in new[] { "dbo.VibEmp", "dbo.branch" })
        {
            Assert.Equal(1, await PermissionAsync(verify, table, "SELECT"));
            Assert.Equal(0, await PermissionAsync(verify, table, "INSERT"));
            Assert.Equal(0, await PermissionAsync(verify, table, "UPDATE"));
            Assert.Equal(0, await PermissionAsync(verify, table, "DELETE"));
        }
    }

    [Fact]
    public async Task Missing_hr_table_is_source_unavailable_and_logs_only_error_number_and_correlation_id()
    {
        await using var database = await ScratchDatabase.CreateAsync(createHrTables: false);
        await using var context = database.AppContext();
        var logger = new CapturingLogger();
        var reader = new EmployeeProfileReader(context, logger);

        var lookup = await reader.LookupAsync("ZTEST-OK", CancellationToken.None);

        Assert.Equal(EmployeeProfileStatus.SourceUnavailable, lookup.Status);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("SqlErrorNumber 208", entry.Message, StringComparison.Ordinal); // invalid object name
        Assert.DoesNotContain("ZTEST-OK", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Invalid object", entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task Permission_denied_on_hr_table_is_source_unavailable()
    {
        // Tables created AFTER the migration ran => no conditional GRANT => pol_app has no SELECT (REQ-3.18).
        await using var database = await ScratchDatabase.CreateAsync(createHrTables: false);
        await database.ExecuteAsync(ScratchDatabase.HrTablesDdl);
        await using var context = database.AppContext();
        var logger = new CapturingLogger();
        var reader = new EmployeeProfileReader(context, logger);

        var lookup = await reader.LookupAsync("ZTEST-OK", CancellationToken.None);

        Assert.Equal(EmployeeProfileStatus.SourceUnavailable, lookup.Status);
        Assert.Contains("SqlErrorNumber 229", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }

    private static async Task<int> PermissionAsync(SqlConnection connection, string objectName, string permission)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            EXECUTE AS USER = 'pol_app';
            SELECT HAS_PERMS_BY_NAME(N'{objectName}', N'OBJECT', N'{permission}');
            REVERT;
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed class CapturingLogger : ILogger<EmployeeProfileReader>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private const string Prefix = "pol_t0profile_reader_it_";

        /// <summary>REQ-11.7 minimal DDL: only the REQ-3.12 columns plus br_code (+ active_row to prove REQ-4.18).</summary>
        public const string HrTablesDdl = """
            CREATE TABLE dbo.VibEmp (EmpCode nvarchar(50) NULL, FirstNameTh nvarchar(1000) NULL,
                LastNameTh nvarchar(1000) NULL, und_brcode char(3) NULL, DepartmentID nvarchar(50) NULL);
            CREATE TABLE dbo.branch (br_code char(3) NULL, active_row bit NULL);
            """;

        private ScratchDatabase(string name) => Name = name;

        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync(bool createHrTables)
        {
            var database = new ScratchDatabase(Prefix + Guid.NewGuid().ToString("N"));
            await using (var master = await database.OpenAsync("master"))
            {
                await ExecuteAsync(master, $"EXEC(N'CREATE DATABASE [{database.Name}] COLLATE Thai_100_CI_AS');");
                await ExecuteAsync(master, $"ALTER DATABASE [{database.Name}] SET COMPATIBILITY_LEVEL = 170;");
            }
            // The real pol_app login (docker/bootstrap/01-principals.sql) mapped into the scratch database so the
            // reader runs under the production principal; the migration's GRANT targets this user by name.
            await database.ExecuteAsync("CREATE USER pol_app FOR LOGIN pol_app;");
            // InitialSchema refuses a non-empty target, so the mirror tables land between the previous head and
            // Tier0EmployeeProfile — exactly the production shape where the conditional GRANT (REQ-8.11) fires.
            await database.MigrateAsync(PreviousMigration);
            if (createHrTables)
                await database.ExecuteAsync(HrTablesDdl);
            await database.MigrateAsync();
            return database;
        }

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await OpenAsync();
            await ExecuteAsync(connection, sql, parameters);
        }

        private static async Task ExecuteAsync(
            SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<SqlConnection> OpenAsync(string? database = null)
        {
            var connection = new SqlConnection(ConnectionString(database ?? Name, "sa", "POL_SA_PASSWORD"));
            await connection.OpenAsync();
            return connection;
        }

        public ControlPlaneDbContext AppContext()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(ConnectionString(Name, "pol_app", "POL_APP_PASSWORD"), sql => sql.UseCompatibilityLevel(170))
                .Options;
            return new ControlPlaneDbContext(options, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        }

        private const string PreviousMigration = "20260823132337_Tier0WorkforceEmailIdentity";

        private async Task MigrateAsync(string? target = null)
        {
            // EnableServiceProviderCaching(false): EF's model cache keys on the CONTEXT TYPE, not on
            // ModuleAssemblies — without it, this shares a cached model with every other PolDbContext test
            // in the process and whichever runs first wins, causing a spurious PendingModelChangesWarning
            // here (see EntitySchemaMappingTests for the same guard).
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(ConnectionString(Name, "sa", "POL_SA_PASSWORD"), sql => sql.UseCompatibilityLevel(170))
                .EnableServiceProviderCaching(false)
                .Options;
            await using var context = new PolDbContext(options, ModuleAssemblies());
            await context.GetService<IMigrator>().MigrateAsync(target);
        }

        public async ValueTask DisposeAsync()
        {
            if (!Name.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(Name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Scratch database name is invalid.");
            SqlConnection.ClearAllPools();
            await using var master = await OpenAsync("master");
            await ExecuteAsync(master,
                $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
        }

        private static ModuleAssemblies ModuleAssemblies() => new([
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

        private static string ConnectionString(string database, string user, string passwordEnv) => new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("POL_SQL_SERVER") ?? "localhost,11433",
            InitialCatalog = database,
            UserID = user,
            Password = Environment.GetEnvironmentVariable(passwordEnv)
                ?? throw new InvalidOperationException($"Integration tests need env var '{passwordEnv}'."),
            Encrypt = true,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString;
    }
}
