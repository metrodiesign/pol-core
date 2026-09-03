using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Integration.Tests;

/// <summary>
/// microsoft-oidc-ciam-alignment task 3: the MicrosoftOidcProviderDiscriminator migration against REAL data.
/// Upgrade (REQ-4.5/6.7): pre-discriminator identities seeded on the previous schema come out with
/// Provider='google' and a backfilled, NOT NULL RegistrationAudits.TargetUserId — existing logins resolve by
/// the (google, subject) pair. Rollback (R4/P1-3): Up -> Down -> Up round-trips clean data, and Down() THROWs
/// before any DDL once two providers share a subject (production restores a backup instead — runbook).
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProviderDiscriminatorMigrationTests
{
    private const string PreviousMigration = "20260811024015_AdminDeliveryRuntimeGrants";
    private const string ThisMigration = "20260816162306_MicrosoftOidcProviderDiscriminator";

    [Fact]
    public async Task Upgrade_backfills_google_provider_and_audit_target_user_id_without_dropping_logins()
    {
        var database = $"pol_provider_up_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            var adminId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                // Pre-discriminator rows: no Provider column exists yet, audits carry only the subject.
                await IntegrationDb.ExecAsync(connection, $"""
                    INSERT admin.Users (Id, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                    VALUES ('{adminId}', N'g-admin-sub-1', N'ops@example.com', 1, 0, 0, 1, SYSUTCDATETIME());
                    INSERT merch.Users (Id, Subject, Email, Status, Version, CreatedAt, DisplayName, FirstName,
                        LastName, IdentityType)
                    VALUES ('{userId}', N'g-user-sub-1', N'somchai@example.com', 0, 1, SYSUTCDATETIME(),
                        N'Somchai Jaidee', N'Somchai', N'Jaidee', 1);
                    INSERT merch.RegistrationAudits
                        (Id, Action, ActorSubject, TargetSubject, CorrelationId, OccurredAt)
                    VALUES
                        ('{Guid.NewGuid()}', N'registered', NULL, N'g-user-sub-1', N'corr-up-self', SYSUTCDATETIME()),
                        ('{Guid.NewGuid()}', N'rejected', N'g-admin-sub-1', N'g-user-sub-1', N'corr-up-admin', SYSUTCDATETIME());
                    """);
            }

            await migrator.MigrateAsync();

            await using (var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal("google", Convert.ToString(await IntegrationDb.ScalarAsync(verify,
                    $"SELECT Provider FROM admin.Users WHERE Id = '{adminId}';")));
                Assert.Equal("google", Convert.ToString(await IntegrationDb.ScalarAsync(verify,
                    $"SELECT Provider FROM merch.Users WHERE Id = '{userId}';")));
                // REQ-4.8: backfilled to the matching user, then locked NOT NULL + FK.
                Assert.Equal(userId.ToString().ToLowerInvariant(), Convert.ToString(await IntegrationDb.ScalarAsync(verify,
                    "SELECT LOWER(CONVERT(nvarchar(36), TargetUserId)) FROM merch.RegistrationAudits WHERE CorrelationId = N'corr-up-admin';")));
                // REQ-4.9: admin actions receive the canonical actor id; self-service stays actor-less.
                Assert.Equal(adminId.ToString().ToLowerInvariant(), Convert.ToString(await IntegrationDb.ScalarAsync(verify,
                    "SELECT LOWER(CONVERT(nvarchar(36), ActorAdminId)) FROM merch.RegistrationAudits WHERE CorrelationId = N'corr-up-admin';")));
                Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(verify,
                    "SELECT ActorAdminId FROM merch.RegistrationAudits WHERE CorrelationId = N'corr-up-self';"));
                Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify, """
                    SELECT is_nullable FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'merch.RegistrationAudits') AND name = N'TargetUserId';
                    """)));
                Assert.NotNull(await IntegrationDb.ScalarAsync(verify,
                    "SELECT OBJECT_ID(N'FK_RegistrationAudits_Users_TargetUserId', N'F');"));
                // The historical provider discriminator remains, while migration HEAD extends Admin ownership
                // to the tenant-aware triple. The final Admin index stays filtered on bound subjects.
                Assert.Equal("Provider,TenantId,Subject", Convert.ToString(await IntegrationDb.ScalarAsync(verify, """
                    SELECT STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                    FROM sys.indexes i
                    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id = OBJECT_ID(N'admin.Users')
                      AND i.name = N'IX_Users_Provider_TenantId_Subject' AND ic.key_ordinal > 0;
                    """)));
                Assert.Contains("[Subject] IS NOT NULL", Convert.ToString(await IntegrationDb.ScalarAsync(verify, """
                    SELECT filter_definition FROM sys.indexes
                    WHERE object_id = OBJECT_ID(N'admin.Users') AND name = N'IX_Users_Provider_TenantId_Subject';
                    """)));
            }

            // REQ-4.5/6.7: current EF mappings still resolve both legacy identities by (provider, subject).
            context.ChangeTracker.Clear();
            var admin = await context.Set<Admins.Domain.Users.User>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Provider == "google" && x.Subject == "g-admin-sub-1");
            Assert.Equal(adminId, admin?.Id);

            var merchant = await context.Set<Merchants.Domain.Users.User>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Provider == "google" && x.Subject == "g-user-sub-1");
            Assert.Equal(userId, merchant?.Id);
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Up_down_up_round_trips_when_no_subject_spans_providers()
    {
        var database = $"pol_provider_rt_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();

            await migrator.MigrateAsync();
            await migrator.MigrateAsync(PreviousMigration); // Down: clean data -> guard passes, DDL reversed
            await using (var down = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                Assert.Null(await IntegrationDb.ScalarAsync(down, """
                    SELECT 1 FROM sys.columns
                    WHERE object_id = OBJECT_ID(N'merch.Users') AND name = N'Provider';
                    """));
            await migrator.MigrateAsync(); // Up again

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.NotNull(await IntegrationDb.ScalarAsync(verify, """
                SELECT 1 FROM sys.indexes
                WHERE object_id = OBJECT_ID(N'merch.Users') AND name = N'IX_Users_Provider_Subject';
                """));
        }
        finally
        {
            await DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    public async Task Down_throws_before_any_ddl_once_two_providers_share_a_subject()
    {
        var database = $"pol_provider_dup_{Guid.NewGuid():N}";
        await CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = CreateContext(database);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync();

            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
                await IntegrationDb.ExecAsync(connection, $"""
                    INSERT merch.Users (Id, Provider, Subject, Email, Status, Version, CreatedAt, DisplayName,
                        FirstName, LastName, IdentityType)
                    VALUES
                        ('{Guid.NewGuid()}', N'google', N'shared-sub', N'g@example.com', 0, 1, SYSUTCDATETIME(),
                         N'G User', N'G', N'User', 1),
                        ('{Guid.NewGuid()}', N'microsoft', N'shared-sub', N'm@example.com', 0, 1, SYSUTCDATETIME(),
                         N'M User', N'M', N'User', 1);
                    """);

            var error = await Assert.ThrowsAnyAsync<Exception>(() => migrator.MigrateAsync(PreviousMigration));
            Assert.Contains("Down blocked: duplicate Subject across providers", error.ToString(), StringComparison.Ordinal);

            // The guard fired BEFORE any DDL: the discriminator schema is fully intact.
            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.NotNull(await IntegrationDb.ScalarAsync(verify, """
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'merch.Users') AND name = N'Provider';
                """));
            Assert.NotNull(await IntegrationDb.ScalarAsync(verify,
                "SELECT OBJECT_ID(N'FK_RegistrationAudits_Users_TargetUserId', N'F');"));
            Assert.Contains(ThisMigration, (string)(await IntegrationDb.ScalarAsync(verify,
                "SELECT MAX(MigrationId) FROM dbo.__EFMigrationsHistory;"))!);
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
