using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Governance;
using WorkforceIdentityMigrator;

namespace Architecture.Tests;

[Trait("Category", "Integration")]
public sealed class Tier0WorkforceIdentityMigrationSqlTests
{
    private const string BeforeHistoricalMigration = "20260819145219_WorkforceTenantBinding";
    private const string PreviousMigration = "20260830172117_Tier0EmployeeProfile";
    private const string CurrentMigration = "20260902133906_Tier0MicrosoftTenantAwareIdentity";
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task Pending_history_and_exact_manifest_map_atomically_then_rerun_accepts_later_final_rows()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var legacyId = Guid.NewGuid();
        var inviteId = Guid.NewGuid();
        var googleId = Guid.NewGuid();
        var legacyObjectId = Guid.NewGuid();
        var inviteObjectId = Guid.NewGuid();
        var historicalSubject = Guid.NewGuid().ToString("D");
        var legacyEmail = "\u00A0Legacy.Owner@VIRIYAH.CO.TH\u00A0";
        await database.InsertUserAsync(legacyId, "microsoft", historicalSubject, legacyEmail, tier: 2, status: 2);
        await database.InsertUserAsync(inviteId, "google", null, "invite@example.com");
        await database.InsertUserAsync(googleId, "google", "google-subject", "duplicate@example.com");
        await database.MigrateAsync(PreviousMigration);
        await database.ExecuteAsync("""
            UPDATE admin.Users
            SET EmployeeId = N'KEEP01', FirstName = N'เดิม', LastName = N'คงไว้', Version = 9,
                AuthorizationVersion = 7
            WHERE Id = @id;
            DECLARE @roleId uniqueidentifier = (SELECT TOP (1) Id FROM iam.Roles ORDER BY Id);
            INSERT admin.RoleAssignments (Id, AdminUserId, RoleId, AssignedById, AssignedAt)
            VALUES (@assignmentId, @id, @roleId, @id, SYSUTCDATETIME());
            INSERT admin.MerchantAccess (Id, AdminUserId, MerchantId, AssignedByAdminId, AssignedAt)
            VALUES (@accessId, @id, @merchantId, @id, SYSUTCDATETIME());
            """, ("@id", legacyId), ("@assignmentId", Guid.NewGuid()),
            ("@accessId", Guid.NewGuid()), ("@merchantId", Guid.NewGuid()));
        var googleBefore = await database.UserIdentityBytesAsync(googleId);
        await database.MigrateAsync(CurrentMigration);

        var manifest = new[]
        {
            new ManifestEntry(legacyId, TenantId, legacyObjectId),
            new ManifestEntry(inviteId, TenantId, inviteObjectId),
        };
        var first = await database.RunToolAsync(manifest);

        Assert.Equal(0, first.ExitCode);
        Assert.Contains("completed: snapshot=2 mapped=2 no-op=0", first.Output, StringComparison.Ordinal);
        AssertSensitiveValuesAbsent(first.Output, database.LastManifestPath, legacyId, TenantId, legacyObjectId, legacyEmail);

        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal($"microsoft|{TenantId:D}|{legacyObjectId:D}|2|2|10|7|KEEP01|เดิม|คงไว้",
                await IdentityRowAsync(verify, legacyId));
            Assert.Equal($"microsoft|{TenantId:D}|{inviteObjectId:D}|1|1|2|0|||",
                await IdentityRowAsync(verify, inviteId));
            Assert.Equal(googleBefore, await database.UserIdentityBytesAsync(googleId));
            Assert.Equal(1, await ScalarIntAsync(verify,
                "SELECT COUNT(*) FROM admin.RoleAssignments WHERE AdminUserId = @id;", ("@id", legacyId)));
            Assert.Equal(1, await ScalarIntAsync(verify,
                "SELECT COUNT(*) FROM admin.MerchantAccess WHERE AdminUserId = @id;", ("@id", legacyId)));
            Assert.Equal(2, await ScalarIntAsync(verify, """
                SELECT COUNT(*) FROM admin.UserAudits
                WHERE Action = N'microsoft-email-bind' AND ActorType = N'system'
                  AND ActorId = 'a9000000-0000-4000-8000-000000000001'
                  AND TargetAdminId IS NOT NULL AND CorrelationId = N'approval-correlation-1';
                """));
            Assert.Equal(0, await ScalarIntAsync(verify, """
                SELECT COUNT(*) FROM admin.UserAudits
                WHERE CorrelationId = N'approved-directory-export-1';
                """));
            Assert.Equal(2, await ScalarIntAsync(verify,
                "SELECT SnapshotCount FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;"));
            Assert.Equal(2, await ScalarIntAsync(verify,
                "SELECT COUNT(*) FROM admin.WorkforceTenantIdentitySnapshot;"));
            Assert.Equal("AdminUserId", Convert.ToString(await ScalarAsync(verify, """
                SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY column_id)
                FROM sys.columns WHERE object_id = OBJECT_ID(N'admin.WorkforceTenantIdentitySnapshot');
                """), CultureInfo.InvariantCulture));
            Assert.Equal("Id,CompletedAt,SnapshotCount,MappedCount,NoOpCount",
                Convert.ToString(await ScalarAsync(verify, """
                    SELECT STRING_AGG(name, ',') WITHIN GROUP (ORDER BY column_id)
                    FROM sys.columns WHERE object_id = OBJECT_ID(N'admin.WorkforceTenantIdentityMigrations');
                    """), CultureInfo.InvariantCulture));
            Assert.NotNull(await ScalarAsync(verify,
                "SELECT CompletedAt FROM admin.WorkforceIdentityMigrations WHERE Id = 1;"));
            Assert.NotNull(await ScalarAsync(verify,
                "SELECT CompletedAt FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;"));
        }

        await database.EnsureStartupAsync(TenantId);
        var laterObjectId = Guid.NewGuid();
        await database.ExecuteAsync("""
            INSERT admin.Users
                (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES
                (@id, N'microsoft', @tenantId, @subject, N'duplicate@example.com', 1, 1, 0, 1, SYSUTCDATETIME());
            """, ("@id", Guid.NewGuid()), ("@tenantId", TenantId), ("@subject", laterObjectId.ToString("D")));

        var rerun = await database.RunToolAsync(inputs: EmptyInputs());
        Assert.Equal(0, rerun.ExitCode);
        Assert.Contains("verified: snapshot=2 mapped=2 no-op=0", rerun.Output, StringComparison.Ordinal);
        await using var afterRerun = await database.OpenAsync();
        Assert.Equal(2, await ScalarIntAsync(afterRerun,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'microsoft-email-bind';"));
    }

    [Fact]
    public async Task Existing_exact_final_tuple_is_a_no_op_and_divergent_manifest_never_overwrites_it()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(CurrentMigration);
        var adminId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        await database.ExecuteAsync(
            "UPDATE admin.WorkforceIdentityMigrations SET CompletedAt = SYSUTCDATETIME() WHERE Id = 1;");
        await database.ExecuteAsync(
            "INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (1, @tenantId);", ("@tenantId", TenantId));
        await database.ExecuteAsync("""
            INSERT admin.Users
                (Id, Provider, TenantId, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES
                (@id, N'microsoft', @tenantId, @subject, NULL, 1, 1, 5, 8, SYSUTCDATETIME());
            """, ("@id", adminId), ("@tenantId", TenantId), ("@subject", objectId.ToString("D")));

        var conflict = await database.RunToolAsync(
            [new ManifestEntry(adminId, TenantId, Guid.NewGuid())]);
        Assert.Equal(1, conflict.ExitCode);
        await using (var unchanged = await database.OpenAsync())
        {
            Assert.Equal(objectId.ToString("D"), Convert.ToString(await ScalarAsync(
                unchanged, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", adminId))));
            Assert.Equal(DBNull.Value, await ScalarAsync(unchanged,
                "SELECT CompletedAt FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;"));
        }

        var exact = await database.RunToolAsync([new ManifestEntry(adminId, TenantId, objectId)]);

        Assert.Equal(0, exact.ExitCode);
        Assert.Contains("snapshot=1 mapped=0 no-op=1", exact.Output, StringComparison.Ordinal);
        await using var verify = await database.OpenAsync();
        Assert.Equal(8, await ScalarIntAsync(verify,
            "SELECT Version FROM admin.Users WHERE Id = @id;", ("@id", adminId)));
        Assert.Equal(5, await ScalarIntAsync(verify,
            "SELECT AuthorizationVersion FROM admin.Users WHERE Id = @id;", ("@id", adminId)));
        Assert.Equal(0, await ScalarIntAsync(verify, "SELECT COUNT(*) FROM admin.UserAudits;"));
    }

    [Fact]
    public async Task Digest_target_evidence_schema_and_coverage_fail_without_any_database_write_or_value_echo()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var adminId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var email = "canary.owner@viriyah.co.th";
        await database.InsertUserAsync(adminId, "microsoft", Guid.NewGuid().ToString("D"), email);
        await database.MigrateAsync(CurrentMigration);
        var entries = new[] { new ManifestEntry(adminId, TenantId, objectId) };

        var wrongDigest = await database.RunToolAsync(entries, mutateInputs: input => input with
        {
            ManifestSha256 = new string('0', 64),
        });
        AssertFailure(wrongDigest, database, adminId, objectId, email);

        var wrongTarget = await database.RunToolAsync(entries, mutateInputs: input => input with
        {
            ApprovedTarget = "wrong.example:1433/wrong-db",
        });
        AssertFailure(wrongTarget, database, adminId, objectId, email);

        var missingEvidence = await database.RunToolAsync(entries, mutateInputs: input => input with
        {
            ApprovalEvidence = " ",
        });
        AssertFailure(missingEvidence, database, adminId, objectId, email);

        var unknownPropertyJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            entries = new[]
            {
                new { adminId, tenantId = TenantId, objectId, forbidden = "canary" },
            },
        });
        var unknownProperty = await database.RunRawManifestAsync(unknownPropertyJson);
        AssertFailure(unknownProperty, database, adminId, objectId, email);

        var duplicatePropertyJson = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":1,"entries":[{"adminId":"{{adminId:D}}","adminId":"{{adminId:D}}","tenantId":"{{TenantId:D}}","objectId":"{{objectId:D}}"}]}""");
        var duplicateProperty = await database.RunRawManifestAsync(duplicatePropertyJson);
        AssertFailure(duplicateProperty, database, adminId, objectId, email);

        var missingPropertyJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            entries = new[] { new { adminId, tenantId = TenantId } },
        });
        var missingProperty = await database.RunRawManifestAsync(missingPropertyJson);
        AssertFailure(missingProperty, database, adminId, objectId, email);

        var emptyObjectIdJson = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            entries = new[] { new { adminId, tenantId = TenantId, objectId = Guid.Empty } },
        });
        var emptyObjectId = await database.RunRawManifestAsync(emptyObjectIdJson);
        AssertFailure(emptyObjectId, database, adminId, objectId, email);

        var malformedObjectIdJson = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"schemaVersion":1,"entries":[{"adminId":"{{adminId:D}}","tenantId":"{{TenantId:D}}","objectId":"not-a-guid"}]}""");
        var malformedObjectId = await database.RunRawManifestAsync(malformedObjectIdJson);
        AssertFailure(malformedObjectId, database, adminId, objectId, email);

        var wrongCoverage = await database.RunToolAsync(
            [new ManifestEntry(Guid.NewGuid(), TenantId, objectId)]);
        AssertFailure(wrongCoverage, database, adminId, objectId, email);

        var startup = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.EnsureStartupAsync(TenantId));
        AssertSensitiveValuesAbsent(startup.Message, adminId, objectId, email, TenantId);

        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal(DBNull.Value, await ScalarAsync(verify,
                "SELECT CompletedAt FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;"));
            Assert.Equal(0, await ScalarIntAsync(verify,
                "SELECT COUNT(*) FROM admin.WorkforceTenantIdentitySnapshot;"));
            Assert.Equal(DBNull.Value, await ScalarAsync(verify,
                "SELECT TenantId FROM admin.Users WHERE Id = @id;", ("@id", adminId)));
            Assert.Equal(0, await ScalarIntAsync(verify, "SELECT COUNT(*) FROM admin.UserAudits;"));
        }

        var success = await database.RunToolAsync(entries);
        Assert.Equal(0, success.ExitCode);
    }

    [Fact]
    public async Task Missing_incomplete_duplicate_and_foreign_manifest_contracts_roll_back_every_first_run_write()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var firstAdminId = Guid.NewGuid();
        var secondAdminId = Guid.NewGuid();
        var firstObjectId = Guid.NewGuid();
        var secondObjectId = Guid.NewGuid();
        await database.InsertUserAsync(
            firstAdminId, "microsoft", Guid.NewGuid().ToString("D"), "first@viriyah.co.th");
        await database.InsertUserAsync(secondAdminId, "google", null, "second@example.com");
        await database.MigrateAsync(CurrentMigration);
        var exact = new[]
        {
            new ManifestEntry(firstAdminId, TenantId, firstObjectId),
            new ManifestEntry(secondAdminId, TenantId, secondObjectId),
        };

        var missing = await database.RunToolAsync(inputs: EmptyInputs());
        AssertFailure(missing, database, firstAdminId, secondAdminId, firstObjectId, secondObjectId, TenantId);

        var incomplete = await database.RunToolAsync([exact[0]]);
        AssertFailure(incomplete, database, firstAdminId, secondAdminId, firstObjectId, secondObjectId, TenantId);

        var duplicateAdmin = await database.RunToolAsync([
            exact[0], new ManifestEntry(firstAdminId, TenantId, secondObjectId),
        ]);
        AssertFailure(duplicateAdmin, database, firstAdminId, secondAdminId, firstObjectId, secondObjectId, TenantId);

        var foreignTenant = Guid.NewGuid();
        var foreign = await database.RunToolAsync([
            new ManifestEntry(firstAdminId, foreignTenant, firstObjectId),
            new ManifestEntry(secondAdminId, foreignTenant, secondObjectId),
        ]);
        AssertFailure(foreign, database, firstAdminId, secondAdminId, firstObjectId, secondObjectId, TenantId, foreignTenant);

        var duplicateTuple = await database.RunToolAsync([
            exact[0], new ManifestEntry(secondAdminId, TenantId, firstObjectId),
        ]);
        AssertFailure(duplicateTuple, database, firstAdminId, secondAdminId, firstObjectId, secondObjectId, TenantId);

        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal(0, await ScalarIntAsync(verify,
                "SELECT COUNT(*) FROM admin.Users WHERE TenantId IS NOT NULL;"));
            Assert.Equal(0, await ScalarIntAsync(verify,
                "SELECT COUNT(*) FROM admin.WorkforceTenantBindings;"));
            Assert.Equal(0, await ScalarIntAsync(verify,
                "SELECT COUNT(*) FROM admin.WorkforceTenantIdentitySnapshot;"));
            Assert.Equal(0, await ScalarIntAsync(verify, "SELECT COUNT(*) FROM admin.UserAudits;"));
            Assert.Equal(DBNull.Value, await ScalarAsync(verify,
                "SELECT CompletedAt FROM admin.WorkforceIdentityMigrations WHERE Id = 1;"));
            Assert.Equal(DBNull.Value, await ScalarAsync(verify,
                "SELECT CompletedAt FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;"));
        }

        Assert.Equal(0, (await database.RunToolAsync(exact)).ExitCode);
    }

    [Fact]
    public async Task Existing_foreign_tenant_singleton_is_rejected_before_identity_or_audit_write()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var adminId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var existingTenant = Guid.NewGuid();
        await database.InsertUserAsync(
            adminId, "microsoft", Guid.NewGuid().ToString("D"), "owner@viriyah.co.th");
        await database.MigrateAsync(CurrentMigration);
        await database.ExecuteAsync(
            "INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (1, @tenantId);",
            ("@tenantId", existingTenant));

        var result = await database.RunToolAsync([new ManifestEntry(adminId, TenantId, objectId)]);

        AssertFailure(result, database, adminId, objectId, TenantId, existingTenant);
        await using var verify = await database.OpenAsync();
        Assert.Equal(DBNull.Value, await ScalarAsync(
            verify, "SELECT TenantId FROM admin.Users WHERE Id = @id;", ("@id", adminId)));
        Assert.Equal(existingTenant, await ScalarAsync(
            verify, "SELECT TenantId FROM admin.WorkforceTenantBindings WHERE Id = 1;"));
        Assert.Equal(0, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.WorkforceTenantIdentitySnapshot;"));
        Assert.Equal(0, await ScalarIntAsync(verify, "SELECT COUNT(*) FROM admin.UserAudits;"));
    }

    [Fact]
    public async Task Concurrent_first_run_tools_serialize_and_append_one_mapping_audit()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var adminId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        await database.InsertUserAsync(
            adminId, "microsoft", Guid.NewGuid().ToString("D"), "owner@viriyah.co.th");
        await database.MigrateAsync(CurrentMigration);
        var entries = new[] { new ManifestEntry(adminId, TenantId, objectId) };

        var results = await Task.WhenAll(
            database.RunToolAsync(entries),
            database.RunToolAsync(entries));

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        await using var verify = await database.OpenAsync();
        Assert.Equal(1, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'microsoft-email-bind';"));
        Assert.Equal(1, await ScalarIntAsync(verify,
            "SELECT MappedCount FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;"));
    }

    [Fact]
    public async Task Empty_first_run_needs_no_manifest_and_startup_initializes_the_tenant_binding()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.MigrateAsync(CurrentMigration);

        var run = await database.RunToolAsync(inputs: EmptyInputs());

        Assert.Equal(0, run.ExitCode);
        Assert.Contains("snapshot=0 mapped=0 no-op=0", run.Output, StringComparison.Ordinal);
        await database.EnsureStartupAsync(TenantId);
        await using var verify = await database.OpenAsync();
        Assert.Equal(TenantId, (Guid)(await ScalarAsync(
            verify, "SELECT TenantId FROM admin.WorkforceTenantBindings WHERE Id = 1;"))!);
    }

    [Fact]
    public async Task Completed_rerun_rejects_snapshot_or_post_completion_identity_drift_without_overwrite()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var adminId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        await database.InsertUserAsync(adminId, "microsoft", Guid.NewGuid().ToString("D"), "owner@viriyah.co.th");
        await database.MigrateAsync(CurrentMigration);
        Assert.Equal(0, (await database.RunToolAsync([new ManifestEntry(adminId, TenantId, objectId)])).ExitCode);
        const string drift = "not-a-canonical-object-id";
        await database.ExecuteAsync(
            "UPDATE admin.Users SET Subject = @subject WHERE Id = @id;", ("@subject", drift), ("@id", adminId));

        var rerun = await database.RunToolAsync(inputs: EmptyInputs());

        Assert.Equal(1, rerun.ExitCode);
        AssertSensitiveValuesAbsent(rerun.Output, drift, adminId, TenantId, objectId);
        await using var verify = await database.OpenAsync();
        Assert.Equal(drift, Convert.ToString(await ScalarAsync(
            verify, "SELECT Subject FROM admin.Users WHERE Id = @id;", ("@id", adminId))));
        Assert.Equal(1, await ScalarIntAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'microsoft-email-bind';"));
    }

    private static WorkforceIdentityMigrationInputs EmptyInputs() => new(null, null, null, null, null, null);

    private static void AssertFailure(
        (int ExitCode, string Output) result, ScratchDatabase database, params object?[] sensitive)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("[workforce-identity] failed: invariant-or-database" + Environment.NewLine, result.Output);
        AssertSensitiveValuesAbsent(result.Output, database.LastManifestPath, sensitive);
    }

    private static void AssertSensitiveValuesAbsent(string output, params object?[] sensitive)
    {
        foreach (var value in sensitive.SelectMany(value => value is object?[] values ? values : [value]))
        {
            if (value is null)
                continue;
            Assert.DoesNotContain(Convert.ToString(value, CultureInfo.InvariantCulture)!, output,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string?> IdentityRowAsync(SqlConnection connection, Guid id) =>
        Convert.ToString(await ScalarAsync(connection, """
            SELECT CONCAT(Provider, '|', CONVERT(nvarchar(36), TenantId), '|', Subject, '|', Tier, '|', Status,
                          '|', Version, '|', AuthorizationVersion, '|', EmployeeId, '|', FirstName, '|', LastName)
            FROM admin.Users WHERE Id = @id;
            """, ("@id", id)), CultureInfo.InvariantCulture);

    private static async Task<object?> ScalarAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return await command.ExecuteScalarAsync();
    }

    private static async Task<int> ScalarIntAsync(
        SqlConnection connection, string sql, params (string Name, object Value)[] parameters) =>
        Convert.ToInt32(await ScalarAsync(connection, sql, parameters), CultureInfo.InvariantCulture);

    private sealed record ManifestEntry(Guid AdminId, Guid TenantId, Guid ObjectId);

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private const string Prefix = "pol_tenant_mapper_it_";
        private readonly List<string> _temporaryFiles = [];
        private ScratchDatabase(string name) => Name = name;
        public string Name { get; }
        public string? LastManifestPath { get; private set; }

        public static async Task<ScratchDatabase> CreateAsync()
        {
            var database = new ScratchDatabase(Prefix + Guid.NewGuid().ToString("N"));
            await using var master = await database.OpenAsync("master");
            await ExecuteAsync(master, $"EXEC(N'CREATE DATABASE [{database.Name}] COLLATE Thai_100_CI_AS');");
            await ExecuteAsync(master, $"ALTER DATABASE [{database.Name}] SET COMPATIBILITY_LEVEL = 170;");
            await using var bootstrap = await database.OpenAsync();
            await ExecuteAsync(bootstrap, "CREATE USER pol_app WITHOUT LOGIN;");
            await database.MigrateAsync(BeforeHistoricalMigration);
            return database;
        }

        public async Task InsertUserAsync(
            Guid id, string provider, string? subject, string? email, int tier = 1, int status = 1)
        {
            await using var connection = await OpenAsync();
            await ExecuteAsync(connection, """
                INSERT admin.Users
                    (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
                VALUES (@id, @provider, @subject, @email, @tier, @status, 0, 1, SYSUTCDATETIME());
                """, ("@id", id), ("@provider", provider), ("@subject", (object?)subject ?? DBNull.Value),
                ("@email", (object?)email ?? DBNull.Value), ("@tier", tier), ("@status", status));
        }

        public async Task<(int ExitCode, string Output)> RunToolAsync(
            IReadOnlyCollection<ManifestEntry>? entries = null,
            WorkforceIdentityMigrationInputs? inputs = null,
            Func<WorkforceIdentityMigrationInputs, WorkforceIdentityMigrationInputs>? mutateInputs = null)
        {
            if (inputs is null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1,
                    entries = (entries ?? []).Select(entry => new
                    {
                        adminId = entry.AdminId,
                        tenantId = entry.TenantId,
                        objectId = entry.ObjectId,
                    }),
                });
                inputs = CreateInputs(bytes);
            }
            if (mutateInputs is not null)
                inputs = mutateInputs(inputs);
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            var exitCode = await WorkforceIdentityMigration.RunAsync(
                ConnectionString(Name), inputs, output, CancellationToken.None);
            return (exitCode, output.ToString());
        }

        public Task<(int ExitCode, string Output)> RunRawManifestAsync(byte[] bytes) =>
            RunToolAsync(inputs: CreateInputs(bytes));

        private WorkforceIdentityMigrationInputs CreateInputs(byte[] bytes)
        {
            var path = Path.Combine(Path.GetTempPath(), $"workforce-manifest-{Guid.NewGuid():N}.json");
            File.WriteAllBytes(path, bytes);
            _temporaryFiles.Add(path);
            LastManifestPath = path;
            return new WorkforceIdentityMigrationInputs(
                path,
                Convert.ToHexString(SHA256.HashData(bytes)),
                $"{NormalizeServer(Environment.GetEnvironmentVariable("POL_SQL_SERVER") ?? "localhost,11433")}/{Name}",
                "approved-directory-export-1",
                "approval-correlation-1",
                TenantId.ToString("D"));
        }

        public async Task EnsureStartupAsync(Guid tenantId)
        {
            await using var context = RuntimeContext();
            var store = new WorkforceTenantBindingStore(
                context,
                new ControlPlaneUnitOfWork(context, NoOpSecurityTelemetry.Instance),
                new GovernanceSqlLockManager(context));
            await store.EnsureAsync(tenantId, CancellationToken.None);
        }

        public async Task<string?> UserIdentityBytesAsync(Guid id)
        {
            await using var connection = await OpenAsync();
            return Convert.ToString(await ScalarAsync(connection, """
                SELECT CONCAT(Provider, '|', Subject, '|', Email, '|', Tier, '|', Status, '|',
                              AuthorizationVersion, '|', Version, '|', CreatedAt)
                FROM admin.Users WHERE Id = @id;
                """, ("@id", id)), CultureInfo.InvariantCulture);
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

        public async Task<SqlConnection> OpenAsync(string? database = null)
        {
            var connection = new SqlConnection(ConnectionString(database ?? Name));
            await connection.OpenAsync();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var path in _temporaryFiles)
                File.Delete(path);
            await using var master = await OpenAsync("master");
            await ExecuteAsync(master,
                $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
        }

        private ControlPlaneDbContext RuntimeContext()
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(ConnectionString(Name), sql => sql.UseCompatibilityLevel(170)).Options;
            return new ControlPlaneDbContext(options, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        }

        private PolDbContext MigrationContext()
        {
            // EnableServiceProviderCaching(false): EF's model cache keys on the CONTEXT TYPE, not on
            // ModuleAssemblies — without it, this shares a cached model with every other PolDbContext test
            // in the process and whichever runs first wins, causing a spurious PendingModelChangesWarning
            // here (see EntitySchemaMappingTests for the same guard).
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(ConnectionString(Name), sql => sql.UseCompatibilityLevel(170))
                .EnableServiceProviderCaching(false).Options;
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
            typeof(Governance.Infrastructure.GovernanceModuleRegistration).Assembly,
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration).Assembly,
        ]);

        private static string NormalizeServer(string value)
        {
            var separator = value.LastIndexOf(',');
            return separator < 0 ? value : value[..separator] + ":" + value[(separator + 1)..];
        }

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
