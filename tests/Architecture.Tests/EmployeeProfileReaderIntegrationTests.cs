using System.Data.Common;
using Admins.Application.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;

namespace Architecture.Tests;

[Trait("Category", "Integration")]
public sealed class EmployeeProfileReaderIntegrationTests
{
    [Fact]
    public async Task Production_reader_uses_one_exact_parameterized_query_and_maps_cardinality_and_names()
    {
        await using var database = await ScratchDatabase.CreateAsync(createHrTableBeforeMigration: true);
        await database.ExecuteAsync("""
            INSERT dbo.VibEmp (EmpCode, FirstNameTh, LastNameTh) VALUES
                (N'ZTEST-OK', N' ชื่อทดสอบ ', N' นามสกุลทดสอบ '),
                (N'ZTEST-DUP', N'a', N'b'),
                (N'ZTEST-DUP', N'c', N'd'),
                (N'ZTEST-NOFIRST', N' ', N'x'),
                (N'ZTEST-NOLAST', N'x', NULL),
                (N'ZTEST-LONG', REPLICATE(N'ก', 501), N'x');
            """);
        var capture = new CommandCaptureInterceptor();
        await using var context = database.AppContext(capture);
        var reader = new EmployeeProfileReader(context, new CapturingLogger());

        var found = await reader.LookupAsync("ZTEST-OK", CancellationToken.None);

        Assert.Equal(EmployeeProfileStatus.Found, found.Status);
        Assert.Equal(new EmployeeProfile("ชื่อทดสอบ", "นามสกุลทดสอบ"), found.Profile);
        Assert.Equal(EmployeeProfileStatus.Missing,
            (await reader.LookupAsync("ZTEST-NONE", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Invalid,
            (await reader.LookupAsync("ZTEST-DUP", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Invalid,
            (await reader.LookupAsync("ZTEST-NOFIRST", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Invalid,
            (await reader.LookupAsync("ZTEST-NOLAST", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Invalid,
            (await reader.LookupAsync("ZTEST-LONG", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Missing,
            (await reader.LookupAsync("ZTEST-O", CancellationToken.None)).Status);
        Assert.Equal(EmployeeProfileStatus.Missing,
            (await reader.LookupAsync("ZTEST-%", CancellationToken.None)).Status);

        Assert.Equal(8, capture.Commands.Count);
        Assert.All(capture.Commands, command =>
        {
            Assert.Contains("SELECT TOP (2) EmpCode, FirstNameTh, LastNameTh", command.Text, StringComparison.Ordinal);
            Assert.Contains("WHERE EmpCode = @employeeId", command.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("ZTEST-", command.Text, StringComparison.Ordinal);
            Assert.Equal(["@employeeId"], command.ParameterNames);
        });

        await using var verify = await database.OpenAsync();
        Assert.Equal(1, await PermissionAsync(verify, "SELECT"));
        foreach (var permission in new[] { "INSERT", "UPDATE", "DELETE", "ALTER", "CONTROL" })
            Assert.Equal(0, await PermissionAsync(verify, permission));
    }

    [Fact]
    public async Task Missing_or_ungranted_table_is_unavailable_with_redacted_log()
    {
        await using var missingDatabase = await ScratchDatabase.CreateAsync(createHrTableBeforeMigration: false);
        await using (var context = missingDatabase.AppContext())
        {
            var logger = new CapturingLogger();
            var lookup = await new EmployeeProfileReader(context, logger)
                .LookupAsync("ZTEST-MISSING", CancellationToken.None);
            Assert.Equal(EmployeeProfileStatus.SourceUnavailable, lookup.Status);
            AssertRedacted(logger, 208, "ZTEST-MISSING");
        }

        await using var deniedDatabase = await ScratchDatabase.CreateAsync(createHrTableBeforeMigration: false);
        await deniedDatabase.ExecuteAsync(ScratchDatabase.HrTableDdl);
        await using (var context = deniedDatabase.AppContext())
        {
            var logger = new CapturingLogger();
            var lookup = await new EmployeeProfileReader(context, logger)
                .LookupAsync("ZTEST-DENIED", CancellationToken.None);
            Assert.Equal(EmployeeProfileStatus.SourceUnavailable, lookup.Status);
            AssertRedacted(logger, 229, "ZTEST-DENIED");
        }
    }

    [Fact]
    public async Task Sql_command_timeout_is_unavailable_and_request_cancellation_propagates()
    {
        await using var database = await ScratchDatabase.CreateAsync(createHrTableBeforeMigration: true);
        await database.ExecuteAsync(
            "INSERT dbo.VibEmp (EmpCode, FirstNameTh, LastNameTh) VALUES (N'ZTEST-TIMEOUT', N'a', N'b');");

        await using var blocker = await database.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = (SqlTransaction)transaction;
            lockCommand.CommandText = "SELECT COUNT(*) FROM dbo.VibEmp WITH (TABLOCKX, HOLDLOCK);";
            _ = await lockCommand.ExecuteScalarAsync();
        }

        await using (var context = database.AppContext())
        {
            context.Database.SetCommandTimeout(1);
            var logger = new CapturingLogger();
            var lookup = await new EmployeeProfileReader(context, logger)
                .LookupAsync("ZTEST-TIMEOUT", CancellationToken.None);
            Assert.Equal(EmployeeProfileStatus.SourceUnavailable, lookup.Status);
            AssertRedacted(logger, -2, "ZTEST-TIMEOUT");
        }

        await transaction.RollbackAsync();

        await using var cancelledContext = database.AppContext();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new EmployeeProfileReader(cancelledContext, new CapturingLogger())
                .LookupAsync("ZTEST-CANCEL", cancellation.Token));
    }

    private static void AssertRedacted(CapturingLogger logger, int number, string employeeId)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains($"SqlErrorNumber {number}", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(employeeId, entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    private static async Task<int> PermissionAsync(SqlConnection connection, string permission)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            EXECUTE AS USER = 'pol_app';
            SELECT HAS_PERMS_BY_NAME(N'dbo.VibEmp', N'OBJECT', N'{permission}');
            REVERT;
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private sealed record CapturedCommand(string Text, string[] ParameterNames);

    private sealed class CommandCaptureInterceptor : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        private void Capture(DbCommand command) => Commands.Add(new CapturedCommand(
            command.CommandText,
            command.Parameters.Cast<DbParameter>().Select(parameter => parameter.ParameterName).ToArray()));
    }

    private sealed class CapturingLogger : ILogger<EmployeeProfileReader>
    {
        public readonly List<(LogLevel Level, string Message, Exception? Exception)> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private const string Prefix = "pol_employee_profile_reader_it_";
        private const string PreviousMigration = "20260823132337_Tier0WorkforceEmailIdentity";

        public const string HrTableDdl = """
            CREATE TABLE dbo.VibEmp (
                EmpCode nvarchar(50) NULL,
                FirstNameTh nvarchar(1000) NULL,
                LastNameTh nvarchar(1000) NULL);
            """;

        private ScratchDatabase(string name) => Name = name;
        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync(bool createHrTableBeforeMigration)
        {
            var database = new ScratchDatabase(Prefix + Guid.NewGuid().ToString("N"));
            await using (var master = await database.OpenAsync("master"))
            {
                await ExecuteAsync(master, $"EXEC(N'CREATE DATABASE [{database.Name}] COLLATE Thai_100_CI_AS');");
                await ExecuteAsync(master, $"ALTER DATABASE [{database.Name}] SET COMPATIBILITY_LEVEL = 170;");
            }
            await database.ExecuteAsync("CREATE USER pol_app FOR LOGIN pol_app;");
            await database.MigrateAsync(PreviousMigration);
            if (createHrTableBeforeMigration)
                await database.ExecuteAsync(HrTableDdl);
            await database.MigrateAsync();
            return database;
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = await OpenAsync();
            await ExecuteAsync(connection, sql);
        }

        private static async Task ExecuteAsync(SqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<SqlConnection> OpenAsync(string? database = null)
        {
            var connection = new SqlConnection(ConnectionString(database ?? Name, "sa", "POL_SA_PASSWORD"));
            await connection.OpenAsync();
            return connection;
        }

        public ControlPlaneDbContext AppContext(DbCommandInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(ConnectionString(Name, "pol_app", "POL_APP_PASSWORD"), sql => sql.UseCompatibilityLevel(170));
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            return new ControlPlaneDbContext(
                options.Options, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        }

        private async Task MigrateAsync(string? target = null)
        {
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(ConnectionString(Name, "sa", "POL_SA_PASSWORD"), sql => sql.UseCompatibilityLevel(170))
                .EnableServiceProviderCaching(false)
                .Options;
            await using var context = new PolDbContext(options, ModuleAssemblies());
            await context.GetService<IMigrator>().MigrateAsync(target);
        }

        public async ValueTask DisposeAsync()
        {
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
            typeof(Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
        ]);

        private static string ConnectionString(string database, string user, string passwordEnvironment) =>
            new SqlConnectionStringBuilder
            {
                DataSource = Environment.GetEnvironmentVariable("POL_SQL_SERVER") ?? "localhost,11433",
                InitialCatalog = database,
                UserID = user,
                Password = Environment.GetEnvironmentVariable(passwordEnvironment)
                    ?? throw new InvalidOperationException($"Integration tests need env var '{passwordEnvironment}'."),
                Encrypt = true,
                TrustServerCertificate = true,
                Pooling = false,
            }.ConnectionString;
    }
}
