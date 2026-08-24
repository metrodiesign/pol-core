using System.Globalization;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Governance;
using WorkforceIdentityMigrator;

namespace Architecture.Tests;

[Trait("Category", "Integration")]
public sealed class Tier0WorkforceIdentityMigrationSqlTests
{
    private const string PreviousMigration = "20260819145219_WorkforceTenantBinding";
    private const string CurrentMigration = "20260823132337_Tier0WorkforceEmailIdentity";
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task Real_sql_serializes_bind_and_jit_and_never_width_folds_email_ownership()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var bindOwner = Guid.NewGuid();
        var widthOwner = Guid.NewGuid();
        await database.InsertUserAsync(
            bindOwner, "microsoft", null, "\u00A0Employee@VIRIYAH.CO.TH\u00A0");
        await database.InsertUserAsync(
            widthOwner, "google", null, "\uFF58@viriyah.co.th");
        await database.MigrateAsync(CurrentMigration);
        Assert.Equal(0, (await database.RunToolAsync()).ExitCode);
        Assert.Equal(0, (await database.RunToolAsync()).ExitCode);
        await database.EnsureStartupAsync(TenantId);

        var binds = await Task.WhenAll(
            ResolveAsync(database, "employee@viriyah.co.th", "bind-a"),
            ResolveAsync(database, "EMPLOYEE@VIRIYAH.CO.TH", "bind-b"));
        Assert.All(binds, result =>
        {
            Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
            Assert.Equal(bindOwner, result.Resolution!.AdminId);
        });

        var jits = await Task.WhenAll(
            ResolveAsync(database, "new.employee@viriyah.co.th", "jit-a"),
            ResolveAsync(database, "NEW.EMPLOYEE@VIRIYAH.CO.TH", "jit-b"));
        Assert.All(jits, result => Assert.Equal(ResolveOutcome.Resolved, result.Outcome));
        Assert.Equal(jits[0].Resolution!.AdminId, jits[1].Resolution!.AdminId);

        var widthFold = await ResolveAsync(database, "x@viriyah.co.th", "width-fold");
        Assert.Equal(ResolveOutcome.IdentityConflict, widthFold.Outcome);

        await using var verify = await database.OpenAsync();
        Assert.Equal("employee@viriyah.co.th", await ScalarStringAsync(
            verify, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", bindOwner)));
        Assert.Equal(1, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'microsoft-email-bind';"));
        Assert.Equal(1, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'jit-provision';"));
        Assert.Equal(1, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.Users WHERE WorkforceEmailKey = N'new.employee@viriyah.co.th';"));
        Assert.Equal(0, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.RoleAssignments WHERE AdminUserId = @id;",
            ("@id", jits[0].Resolution!.AdminId)));
        Assert.Equal(0, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.MerchantAccess WHERE AdminUserId = @id;",
            ("@id", jits[0].Resolution!.AdminId)));
        Assert.Equal(DBNull.Value, await ScalarAsync(
            verify, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", widthOwner)));

        await Assert.ThrowsAsync<SqlException>(() => database.ExecuteAsync(
            """
            INSERT admin.Users
                (Id, Provider, Subject, Email, WorkforceEmailKey, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES
                (@id, N'google', NULL, N'unique-probe@example.invalid', N'EMPLOYEE@VIRIYAH.CO.TH', 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid())));
    }

    [Fact]
    public async Task Valid_uuid_and_canonical_no_op_convert_preserve_state_and_guarded_down_restores_legacy()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var convertedId = Guid.NewGuid();
        var noOpId = Guid.NewGuid();
        var googleId = Guid.NewGuid();
        var legacy = Guid.NewGuid().ToString("D").ToUpperInvariant();
        await database.InsertUserAsync(convertedId, "microsoft", legacy, "  Converted@VIRIYAH.CO.TH  ", tier: 2);
        await database.InsertUserAsync(noOpId, "microsoft", "noop@viriyah.co.th", "noop@viriyah.co.th");
        await database.InsertUserAsync(googleId, "google", "google-subject", "Google@VIRIYAH.CO.TH");
        await database.ExecuteAsync(
            """
            DECLARE @roleId uniqueidentifier = (SELECT TOP (1) Id FROM iam.Roles ORDER BY Id);
            INSERT admin.RoleAssignments (Id, AdminUserId, RoleId, AssignedById, AssignedAt)
            VALUES (@assignmentId, @adminId, @roleId, @adminId, SYSUTCDATETIME());
            INSERT admin.MerchantAccess (Id, AdminUserId, MerchantId, AssignedByAdminId, AssignedAt)
            VALUES (@accessId, @adminId, @merchantId, @adminId, SYSUTCDATETIME());
            """,
            ("@assignmentId", Guid.NewGuid()), ("@adminId", convertedId),
            ("@accessId", Guid.NewGuid()), ("@merchantId", Guid.NewGuid()));

        await database.MigrateAsync(CurrentMigration);
        var (exitCode, output) = await database.RunToolAsync();

        Assert.Equal(0, exitCode);
        Assert.Contains("snapshot=2 converted=1 no-op=1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("converted@viriyah.co.th", output, StringComparison.OrdinalIgnoreCase);
        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal("converted@viriyah.co.th", await ScalarStringAsync(
                verify, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", convertedId)));
            Assert.Equal("noop@viriyah.co.th", await ScalarStringAsync(
                verify, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", noOpId)));
            Assert.Equal("google@viriyah.co.th", await ScalarStringAsync(
                verify, "SELECT WorkforceEmailKey FROM admin.Users WHERE Id = @id;", ("@id", googleId)));
            Assert.Equal(2, await ScalarIntAsync(
                verify, "SELECT SnapshotCount FROM admin.WorkforceIdentityMigrations WHERE Id = 1;"));
            Assert.NotNull(await ScalarAsync(
                verify, "SELECT CompletedAt FROM admin.WorkforceIdentityMigrations WHERE Id = 1;"));
            Assert.Equal(1, await ScalarIntAsync(
                verify, "SELECT COUNT(*) FROM admin.RoleAssignments WHERE AdminUserId = @id;", ("@id", convertedId)));
            Assert.Equal(1, await ScalarIntAsync(
                verify, "SELECT COUNT(*) FROM admin.MerchantAccess WHERE AdminUserId = @id;", ("@id", convertedId)));
            Assert.Equal(7, await ScalarIntAsync(
                verify, "SELECT AuthorizationVersion FROM admin.Users WHERE Id = @id;", ("@id", convertedId)));
            Assert.Equal(9, await ScalarIntAsync(
                verify, "SELECT Version FROM admin.Users WHERE Id = @id;", ("@id", convertedId)));
            Assert.Equal(1, await PermissionAsync(verify, "admin.WorkforceIdentityMigrations", "SELECT"));
            Assert.Equal(0, await PermissionAsync(verify, "admin.WorkforceIdentitySubjectRollback", "SELECT"));
        }

        await database.EnsureStartupAsync(TenantId);
        var rerun = await database.RunToolAsync();
        Assert.Equal(0, rerun.ExitCode);
        Assert.Contains("verified", rerun.Output, StringComparison.Ordinal);

        await database.MigrateAsync(PreviousMigration);
        await using var down = await database.OpenAsync();
        Assert.Equal(legacy, await ScalarStringAsync(
            down, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", convertedId)));
        Assert.Equal("noop@viriyah.co.th", await ScalarStringAsync(
            down, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", noOpId)));
        Assert.Equal(DBNull.Value, await ScalarAsync(
            down, "SELECT OBJECT_ID(N'admin.WorkforceIdentityMigrations', N'U');"));
        Assert.Equal(DBNull.Value, await ScalarAsync(
            down, "SELECT COL_LENGTH(N'admin.Users', N'WorkforceEmailKey');"));
        Assert.Equal(1, await ScalarIntAsync(
            down, "SELECT COUNT(*) FROM admin.RoleAssignments WHERE AdminUserId = @id;", ("@id", convertedId)));
        Assert.Equal(1, await ScalarIntAsync(
            down, "SELECT COUNT(*) FROM admin.MerchantAccess WHERE AdminUserId = @id;", ("@id", convertedId)));
    }

    [Theory]
    [InlineData("invalid-email")]
    [InlineData("duplicate-email")]
    [InlineData("unknown-subject")]
    public async Task Invalid_pending_sets_abort_atomically_and_block_startup_without_value_echo(string scenario)
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var firstId = Guid.NewGuid();
        var firstSubject = scenario == "unknown-subject" ? "subject-canary" : Guid.NewGuid().ToString("D");
        var firstEmail = scenario == "invalid-email" ? "email-canary@example.invalid" : "owner@viriyah.co.th";
        await database.InsertUserAsync(firstId, "microsoft", firstSubject, firstEmail);
        if (scenario == "duplicate-email")
            await database.InsertUserAsync(
                Guid.NewGuid(), "google", "google-subject", "\tOWNER@VIRIYAH.CO.TH\t");
        await database.MigrateAsync(CurrentMigration);

        var (exitCode, output) = await database.RunToolAsync();

        Assert.Equal(1, exitCode);
        Assert.Equal("[workforce-identity] failed: invariant-or-database" + Environment.NewLine, output);
        Assert.DoesNotContain(firstSubject, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstEmail, output, StringComparison.OrdinalIgnoreCase);
        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal(firstSubject, await ScalarStringAsync(
                verify, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", firstId)));
            Assert.Equal(0, await ScalarIntAsync(
                verify, "SELECT COUNT(*) FROM admin.Users WHERE WorkforceEmailKey IS NOT NULL;"));
            Assert.Equal(DBNull.Value, await ScalarAsync(
                verify, "SELECT CompletedAt FROM admin.WorkforceIdentityMigrations WHERE Id = 1;"));
        }

        var startup = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.EnsureStartupAsync(TenantId));
        Assert.DoesNotContain(firstSubject, startup.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstEmail, startup.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("uuid-subject")]
    [InlineData("unknown-subject")]
    [InlineData("bad-key")]
    public async Task Completed_state_rerun_and_startup_reject_identity_or_key_drift(string scenario)
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var adminId = Guid.NewGuid();
        await database.InsertUserAsync(
            adminId, "microsoft", Guid.NewGuid().ToString("D"), "drift-owner@viriyah.co.th");
        await database.MigrateAsync(CurrentMigration);
        Assert.Equal(0, (await database.RunToolAsync()).ExitCode);

        var canary = scenario switch
        {
            "uuid-subject" => Guid.NewGuid().ToString("D"),
            "unknown-subject" => "unknown-subject-canary",
            _ => "bad-key-canary@viriyah.co.th",
        };
        await database.ExecuteAsync(
            scenario == "bad-key"
                ? "UPDATE admin.Users SET WorkforceEmailKey = @value WHERE Id = @id;"
                : "UPDATE admin.Users SET Subject = @value WHERE Id = @id;",
            ("@value", canary), ("@id", adminId));

        var rerun = await database.RunToolAsync();
        Assert.Equal(1, rerun.ExitCode);
        Assert.DoesNotContain(canary, rerun.Output, StringComparison.OrdinalIgnoreCase);
        var startup = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.EnsureStartupAsync(TenantId));
        Assert.DoesNotContain(canary, startup.Message, StringComparison.OrdinalIgnoreCase);

        if (scenario == "bad-key")
        {
            await database.MigrateAsync(PreviousMigration);
            await using var rolledBack = await database.OpenAsync();
            Assert.Equal(DBNull.Value, await ScalarAsync(
                rolledBack, "SELECT OBJECT_ID(N'admin.WorkforceIdentityMigrations', N'U');"));
            return;
        }

        var down = await Assert.ThrowsAnyAsync<Exception>(() => database.MigrateAsync(PreviousMigration));
        Assert.DoesNotContain(canary, down.ToString(), StringComparison.OrdinalIgnoreCase);
        await using var verify = await database.OpenAsync();
        Assert.NotNull(await ScalarAsync(
            verify, "SELECT OBJECT_ID(N'admin.WorkforceIdentityMigrations', N'U');"));
    }

    private static async Task<object?> ScalarAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string?> ScalarStringAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToString(await ScalarAsync(connection, sql, parameters), CultureInfo.InvariantCulture);

    private static async Task<int> ScalarIntAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(connection, sql, parameters), CultureInfo.InvariantCulture);

    private static Task<int> PermissionAsync(SqlConnection connection, string objectName, string permission) =>
        ScalarIntAsync(connection,
            $"""
            EXECUTE AS USER = 'pol_app';
            SELECT HAS_PERMS_BY_NAME(N'{objectName}', N'OBJECT', N'{permission}');
            REVERT;
            """);

    private static async Task<ResolveResult> ResolveAsync(
        ScratchDatabase database, string email, string correlationId)
    {
        await using var context = database.RuntimeContext();
        var telemetry = NoOpSecurityTelemetry.Instance;
        var locks = new GovernanceSqlLockManager(context);
        var handler = new ResolveMicrosoftAdminHandler(
            new UserRepository(context, NullLogger<UserRepository>.Instance, telemetry, locks),
            new RoleRepository(context),
            new AuditWriter(context),
            ConflictRecovery.Instance,
            new ControlPlaneUnitOfWork(context, telemetry),
            FixedClock.Instance);
        return await handler.Handle(
            new ResolveMicrosoftAdminCommand(email, correlationId), CancellationToken.None);
    }

    private sealed class ConflictRecovery : IAdminIdentityRecoveryReader
    {
        public static readonly ConflictRecovery Instance = new();

        public Task<ResolveResult> ResolveAfterConflictAsync(
            string canonicalEmail, CancellationToken cancellationToken) =>
            Task.FromResult(ResolveResult.IdentityConflict);
    }

    private sealed class FixedClock : IClock
    {
        public static readonly FixedClock Instance = new();
        public DateTime UtcNow => new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private const string Prefix = "pol_tier0_migration_it_";

        private ScratchDatabase(string name) => Name = name;

        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync()
        {
            var database = new ScratchDatabase(Prefix + Guid.NewGuid().ToString("N"));
            await using var master = await database.OpenAsync("master");
            await ExecuteAsync(master, $"EXEC(N'CREATE DATABASE [{database.Name}] COLLATE Thai_100_CI_AS');");
            await ExecuteAsync(master, $"ALTER DATABASE [{database.Name}] SET COMPATIBILITY_LEVEL = 170;");
            await using var bootstrap = await database.OpenAsync();
            await ExecuteAsync(bootstrap, "CREATE USER pol_app WITHOUT LOGIN;");
            await database.MigrateAsync(PreviousMigration);
            return database;
        }

        public async Task InsertUserAsync(
            Guid id, string provider, string? subject, string email, int tier = 1)
        {
            await using var connection = await OpenAsync();
            await ExecuteAsync(connection,
                """
                INSERT admin.Users
                    (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                VALUES (@id, @provider, @subject, @email, @tier, 1, 7, 9, SYSUTCDATETIME());
                """,
                ("@id", id), ("@provider", provider), ("@subject", (object?)subject ?? DBNull.Value),
                ("@email", email), ("@tier", tier));
        }

        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await OpenAsync();
            await ExecuteAsync(connection, sql, parameters);
        }

        public async Task MigrateAsync(string migration)
        {
            await using var context = MigrationContext();
            await context.GetService<IMigrator>().MigrateAsync(migration);
        }

        public async Task<(int ExitCode, string Output)> RunToolAsync()
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            var exitCode = await WorkforceIdentityMigration.RunAsync(
                ConnectionString(Name), output, CancellationToken.None);
            return (exitCode, output.ToString());
        }

        public async Task EnsureStartupAsync(Guid tenantId)
        {
            await using var context = RuntimeContext();
            var unitOfWork = new ControlPlaneUnitOfWork(context, NoOpSecurityTelemetry.Instance);
            var store = new WorkforceTenantBindingStore(
                context, unitOfWork, new GovernanceSqlLockManager(context));
            await store.EnsureAsync(tenantId, CancellationToken.None);
        }

        public async Task<SqlConnection> OpenAsync(string? database = null)
        {
            var connection = new SqlConnection(ConnectionString(database ?? Name));
            await connection.OpenAsync();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            if (!Name.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(Name[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Scratch database name is invalid.");
            await using var master = await OpenAsync("master");
            await ExecuteAsync(master,
                $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
        }

        public ControlPlaneDbContext RuntimeContext()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(ConnectionString(Name), sql => sql.UseCompatibilityLevel(170))
                .Options;
            return new ControlPlaneDbContext(
                options, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        }

        private PolDbContext MigrationContext()
        {
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(ConnectionString(Name), sql => sql.UseCompatibilityLevel(170))
                .Options;
            return new PolDbContext(options, ModuleAssemblies());
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

        private static string ConnectionString(string database) => new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("POL_SQL_SERVER") ?? "localhost,11433",
            InitialCatalog = database,
            UserID = "sa",
            Password = Environment.GetEnvironmentVariable("POL_SA_PASSWORD")
                ?? throw new InvalidOperationException("Integration tests need POL_SA_PASSWORD."),
            Encrypt = true,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString;

        private static async Task ExecuteAsync(
            SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
    }
}
