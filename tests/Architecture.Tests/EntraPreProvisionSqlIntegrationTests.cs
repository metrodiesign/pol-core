using System.Text.Json;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Governance.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Governance;

namespace Architecture.Tests;

[Trait("Category", "Integration")]
public sealed class EntraPreProvisionSqlIntegrationTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 5, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task Production_tenant_store_serializes_concurrent_initializers_and_rejects_drift()
    {
        await using var database = await ScratchDatabase.CreateAsync();

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => EnsureTenantAsync(database.Name, TenantId)));

        await using (var verify = NewControlPlaneContext(database.Name))
        {
            var binding = await verify.WorkforceTenantBindings.AsNoTracking().SingleAsync();
            Assert.Equal(1, binding.Id);
            Assert.Equal(TenantId, binding.TenantId);
        }

        var drift = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EnsureTenantAsync(database.Name, drift));
        Assert.DoesNotContain(TenantId.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(drift.ToString("D"), error.Message, StringComparison.OrdinalIgnoreCase);

        await using var final = NewControlPlaneContext(database.Name);
        Assert.Equal(1, await final.WorkforceTenantBindings.CountAsync());
        Assert.Equal(TenantId, (await final.WorkforceTenantBindings.SingleAsync()).TenantId);
    }

    [Fact]
    public async Task Production_handler_allows_one_winner_for_identity_and_target_races()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await EnsureTenantAsync(database.Name, TenantId);
        var actor = User.SelfProvision(User.GoogleProvider, $"actor-{Guid.NewGuid():N}", UniqueEmail("actor"), Now);
        var first = User.CreateScoped(UniqueEmail("first"), Now);
        var second = User.CreateScoped(UniqueEmail("second"), Now);
        var sharedTarget = User.CreateScoped(UniqueEmail("shared"), Now);
        await SeedUsersAsync(database.Name, actor, first, second, sharedTarget);

        var sharedIdentity = Guid.NewGuid();
        var identityRace = await Task.WhenAll(
            BindAsync(database.Name, Command(actor, first, sharedIdentity, "identity-a", "identity-a")),
            BindAsync(database.Name, Command(actor, second, sharedIdentity, "identity-b", "identity-b")));

        Assert.Single(identityRace, x => x.Result is not null);
        var identityLoser = Assert.Single(identityRace, x => x.Error is not null).Error;
        Assert.Equal("microsoft_identity_already_bound", Assert.IsType<ConflictException>(identityLoser).Code);

        var firstObjectId = Guid.NewGuid();
        var secondObjectId = Guid.NewGuid();
        var targetRace = await Task.WhenAll(
            BindAsync(database.Name, Command(actor, sharedTarget, firstObjectId, "target-a", "target-a")),
            BindAsync(database.Name, Command(actor, sharedTarget, secondObjectId, "target-b", "target-b")));

        Assert.Single(targetRace, x => x.Result is not null);
        var targetLoser = Assert.Single(targetRace, x => x.Error is not null).Error;
        Assert.Equal("state_conflict", Assert.IsType<ConcurrencyConflictException>(targetLoser).Code);

        await using var verify = NewControlPlaneContext(database.Name);
        var users = await verify.Users.AsNoTracking()
            .Where(x => x.Id == first.Id || x.Id == second.Id || x.Id == sharedTarget.Id)
            .ToListAsync();
        Assert.Equal(1, users.Count(x => x.Subject == sharedIdentity.ToString("D")));
        var persistedSharedTarget = Assert.Single(users, x => x.Id == sharedTarget.Id);
        Assert.Contains(persistedSharedTarget.Subject, new[]
        {
            firstObjectId.ToString("D"),
            secondObjectId.ToString("D"),
        });
        Assert.Equal(2, persistedSharedTarget.Version);
        Assert.Equal(2, await verify.AuditRecords.CountAsync(x =>
            x.Action == "admin.microsoft-identity.preprovisioned"));
        Assert.Equal(2, await verify.OperationRecords.CountAsync(x =>
            x.Operation == "PreProvisionMicrosoftIdentity"));
    }

    [Fact]
    public async Task Production_handler_rolls_back_identity_audit_and_operation_when_audit_save_fails()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await EnsureTenantAsync(database.Name, TenantId);
        var actor = User.SelfProvision(User.GoogleProvider, $"actor-{Guid.NewGuid():N}", UniqueEmail("actor"), Now);
        var target = User.CreateScoped(UniqueEmail("target"), Now);
        await SeedUsersAsync(database.Name, actor, target);
        await database.ExecuteAsync(
            """
            CREATE TRIGGER admin.TR_AuditRecords_Reject_IntegrationTest
            ON admin.AuditRecords
            INSTEAD OF INSERT
            AS
                THROW 51000, 'forced audit persistence failure', 1;
            """);

        var outcome = await BindAsync(
            database.Name,
            Command(actor, target, Guid.NewGuid(), "rollback", "rollback"));

        Assert.Null(outcome.Result);
        Assert.NotNull(outcome.Error);
        await using var verify = NewControlPlaneContext(database.Name);
        var persisted = await verify.Users.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        Assert.Equal(User.GoogleProvider, persisted.Provider);
        Assert.Null(persisted.Subject);
        Assert.Equal(1, persisted.Version);
        Assert.Equal(0, await verify.AuditHeads.CountAsync());
        Assert.Equal(0, await verify.AuditRecords.CountAsync());
        Assert.Equal(0, await verify.OperationRecords.CountAsync());
    }

    [Fact]
    public async Task Exact_replay_keeps_stored_200_body_and_version_after_production_prune_boundary()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await EnsureTenantAsync(database.Name, TenantId);
        var actor = User.SelfProvision(User.GoogleProvider, $"actor-{Guid.NewGuid():N}", UniqueEmail("actor"), Now);
        var target = User.CreateScoped(UniqueEmail("target"), Now);
        await SeedUsersAsync(database.Name, actor, target);
        var command = Command(actor, target, Guid.NewGuid(), "replay", "replay");

        var first = (await BindAsync(database.Name, command)).Result;
        Assert.NotNull(first);

        await using (var prune = NewControlPlaneContext(database.Name))
        {
            var stored = await prune.OperationRecords.SingleAsync(x =>
                x.Operation == "PreProvisionMicrosoftIdentity");
            Assert.Equal(200, stored.ResponseStatus);
            Assert.Equal(DateTime.MaxValue.Ticks, stored.ExpiresAt.Ticks);
            Assert.Equal(first, JsonSerializer.Deserialize<PreProvisionMicrosoftIdentityResult>(
                stored.ResponseBody!, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

            var expired = OperationRecord.Create(
                actor.Id,
                "ExpiredIntegrationProbe",
                "expired-probe",
                new string('a', 64),
                GovernanceScopeKind.Platform,
                merchantId: null,
                Now.AddDays(-3),
                Now.AddDays(-1));
            expired.Complete(200, "{}", succeeded: true, Now.AddDays(-2));
            prune.OperationRecords.Add(expired);
            await prune.SaveChangesAsync();

            var expiredRecords = await prune.OperationRecords.Where(x => x.ExpiresAt < Now)
                .OrderBy(x => x.ExpiresAt).Take(1000).ToListAsync();
            prune.OperationRecords.RemoveRange(expiredRecords);
            await prune.SaveChangesAsync();
            Assert.False(await prune.OperationRecords.AnyAsync(x => x.Id == expired.Id));
            Assert.True(await prune.OperationRecords.AnyAsync(x => x.Id == stored.Id));
        }

        var replay = (await BindAsync(database.Name, command)).Result;
        Assert.Equal(first, replay);
        Assert.Equal(first!.Version, replay!.Version);

        var caseEquivalent = await Task.WhenAll(
            BindAsync(database.Name, command with
            {
                IdempotencyKey = "identity-case-key",
                CorrelationId = "identity-case-lower",
                ExpectedTargetVersion = first.Version,
            }),
            BindAsync(database.Name, command with
            {
                IdempotencyKey = "IDENTITY-CASE-KEY",
                CorrelationId = "identity-case-upper",
                ExpectedTargetVersion = first.Version,
            }));
        Assert.All(caseEquivalent, outcome =>
        {
            Assert.Null(outcome.Error);
            Assert.Equal(first, outcome.Result);
        });

        await using var verify = NewControlPlaneContext(database.Name);
        Assert.Equal(1, await verify.AuditRecords.CountAsync(x =>
            x.Action == "admin.microsoft-identity.preprovisioned"));
        Assert.Equal(2, await verify.OperationRecords.CountAsync(x =>
            x.Operation == "PreProvisionMicrosoftIdentity"));
    }

    private static PreProvisionMicrosoftIdentityCommand Command(
        User actor,
        User target,
        Guid objectId,
        string key,
        string correlationId) => new(
            target.Id,
            TenantId,
            objectId,
            "HR onboarding",
            actor.Id,
            actor.AuthorizationVersion,
            target.Version,
            correlationId,
            key,
            TenantId);

    private static async Task EnsureTenantAsync(string database, Guid tenantId)
    {
        await using var db = NewControlPlaneContext(database);
        var unitOfWork = new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        var store = new WorkforceTenantBindingStore(db, unitOfWork, new GovernanceSqlLockManager(db));
        await store.EnsureAsync(tenantId, CancellationToken.None);
    }

    private static async Task SeedUsersAsync(string database, params User[] users)
    {
        await using var db = NewControlPlaneContext(database);
        db.Users.AddRange(users);
        await db.SaveChangesAsync();
    }

    private static async Task<BindOutcome> BindAsync(
        string database,
        PreProvisionMicrosoftIdentityCommand command)
    {
        await using var db = NewControlPlaneContext(database);
        var telemetry = NoOpSecurityTelemetry.Instance;
        var unitOfWork = new ControlPlaneUnitOfWork(db, telemetry);
        var locks = new GovernanceSqlLockManager(db);
        var handler = new PreProvisionMicrosoftIdentityHandler(
            new UserRepository(db, NullLogger<UserRepository>.Instance, telemetry, locks),
            new AdminIdentityAuditWriter(new GovernanceAuditAppender(db, locks)),
            new AdminOperationStore(db, locks),
            unitOfWork,
            new FixedClock(Now));
        try
        {
            return new BindOutcome(await handler.Handle(command, CancellationToken.None), null);
        }
        catch (Exception error)
        {
            return new BindOutcome(null, error);
        }
    }

    private static ControlPlaneDbContext NewControlPlaneContext(string database)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(ScratchDatabase.ConnectionString(database), sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new ControlPlaneDbContext(options, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
    }

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.invalid";

    private sealed record BindOutcome(PreProvisionMicrosoftIdentityResult? Result, Exception? Error);

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private const string Prefix = "pol_entra_it_";

        private ScratchDatabase(string name) => Name = name;

        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync()
        {
            var name = $"{Prefix}{Guid.NewGuid():N}";
            ValidateName(name);
            var database = new ScratchDatabase(name);
            var created = false;
            try
            {
                await using (var master = await OpenAsync("master"))
                {
                    await ExecuteAsync(master, $"EXEC(N'CREATE DATABASE [{name}] COLLATE Thai_100_CI_AS');");
                    created = true;
                    await ExecuteAsync(master, $"ALTER DATABASE [{name}] SET COMPATIBILITY_LEVEL = 170;");
                }

                await using (var bootstrap = await OpenAsync(name))
                    await ExecuteAsync(bootstrap, "CREATE USER pol_app WITHOUT LOGIN;");
                await using (var migrations = NewMigrationContext(name))
                    await migrations.GetService<IMigrator>().MigrateAsync();
                return database;
            }
            catch
            {
                if (created)
                    await database.DisposeAsync();
                throw;
            }
        }

        public Task ExecuteAsync(string sql) => ExecuteInDatabaseAsync(Name, sql);

        public async ValueTask DisposeAsync()
        {
            ValidateName(Name);
            await using var master = await OpenAsync("master");
            await ExecuteAsync(master,
                $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN "
                + $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
        }

        public static string ConnectionString(string database) => new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("POL_SQL_SERVER") ?? "localhost,11433",
            InitialCatalog = database,
            UserID = "sa",
            Password = Require("POL_SA_PASSWORD"),
            Encrypt = true,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString;

        private static async Task ExecuteInDatabaseAsync(string database, string sql)
        {
            await using var connection = await OpenAsync(database);
            await ExecuteAsync(connection, sql);
        }

        private static async Task<SqlConnection> OpenAsync(string database)
        {
            var connection = new SqlConnection(ConnectionString(database));
            await connection.OpenAsync();
            return connection;
        }

        private static async Task ExecuteAsync(SqlConnection connection, string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        private static PolDbContext NewMigrationContext(string database)
        {
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(ConnectionString(database), sql => sql.UseCompatibilityLevel(170))
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

        private static string Require(string key) =>
            Environment.GetEnvironmentVariable(key)
            ?? throw new InvalidOperationException($"Integration tests need env var '{key}'.");

        private static void ValidateName(string database)
        {
            if (!database.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(database[Prefix.Length..], "N", out _))
                throw new InvalidOperationException("Scratch database name is invalid.");
        }
    }
}
