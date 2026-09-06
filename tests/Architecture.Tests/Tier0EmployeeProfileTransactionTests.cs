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
using SharedKernel;
using WorkforceIdentityMigrator;

namespace Architecture.Tests;

/// <summary>
/// Runs the tenant-aware Microsoft handler with the real ControlPlane repository, unit of work, SQL applock,
/// HR reader and fresh recovery context against disposable SQL Server databases. All fixture values are synthetic.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Tier0EmployeeProfileTransactionTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly DateTime FirstLogin = new(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SecondLogin = new(2026, 8, 31, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Email_less_jit_and_profile_commit_together_then_refresh_without_authorization_change()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.SeedHrAsync("ZTEST-T1", "สมชาย", "ใจดี");

        var first = await database.ResolveAsync(
            ObjectId, email: null, employeeId: "ZTEST-T1", "corr-1", FirstLogin);

        Assert.Equal(ResolveOutcome.Resolved, first.Outcome);
        var adminId = first.Resolution!.AdminId;
        Assert.Null(first.Resolution.Email);
        Assert.False(first.Resolution.Accessible.IsUnrestricted);
        Assert.Empty(first.Resolution.Accessible.Merchants);
        Assert.Empty(first.Resolution.Permissions);
        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal(
                $"microsoft|{TenantId:D}|{ObjectId:D}|<null>|1|1|2|0",
                await database.IdentityRowAsync(verify, adminId));
            Assert.Equal(
                "ZTEST-T1|สมชาย|ใจดี|2|0|",
                await database.ProfileRowAsync(verify, adminId));
            Assert.Equal("employee-bind,jit-provision", await ScalarAsync(verify,
                "SELECT STRING_AGG(Action, ',') WITHIN GROUP (ORDER BY Action) FROM admin.UserAudits WHERE TargetAdminId = @id;",
                ("@id", adminId)));
            Assert.Equal(0, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT (SELECT COUNT(*) FROM admin.RoleAssignments WHERE AdminUserId = @id) + "
                + "(SELECT COUNT(*) FROM admin.MerchantAccess WHERE AdminUserId = @id);", ("@id", adminId))));
        }

        await database.ExecuteAsync("UPDATE dbo.VibEmp SET FirstNameTh = N'สมหญิง' WHERE EmpCode = N'ZTEST-T1';");
        var second = await database.ResolveAsync(
            ObjectId, "renamed@example.com", "ZTEST-T1", "corr-2", SecondLogin);

        Assert.Equal(ResolveOutcome.Resolved, second.Outcome);
        Assert.Equal(adminId, second.Resolution!.AdminId);
        Assert.Null(second.Resolution.Email); // callback contact never refreshes the persisted contact
        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal(
                "ZTEST-T1|สมหญิง|ใจดี|3|0|" + SecondLogin.ToString("yyyy-MM-dd HH:mm:ss"),
                await database.ProfileRowAsync(verify, adminId));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.UserAudits WHERE TargetAdminId = @id AND Action = N'employee-bind';",
                ("@id", adminId))));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.UserAudits WHERE TargetAdminId = @id AND Action = N'jit-provision';",
                ("@id", adminId))));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.UserAudits WHERE TargetAdminId = @id AND Action = N'employee-profile-sync';",
                ("@id", adminId))));
        }

        var third = await database.ResolveAsync(
            ObjectId, email: null, employeeId: "ZTEST-T1", "corr-3", SecondLogin.AddHours(1));
        Assert.Equal(ResolveOutcome.Resolved, third.Outcome);
        await using (var verify = await database.OpenAsync())
            Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(
                verify, "SELECT Version FROM admin.Users WHERE Id = @id;", ("@id", adminId))));
    }

    [Fact]
    public async Task Exact_identity_replaces_unowned_employee_id_and_preserves_authorization_and_org_state()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.SeedHrAsync("E001", "ชื่อเดิม", "นามสกุลเดิม");
        await database.SeedHrAsync("E002", "ชื่อใหม่", "นามสกุลใหม่");

        var initial = await database.ResolveAsync(
            ObjectId, "synthetic@example.test", "E001", "corr-initial", FirstLogin);
        Assert.Equal(ResolveOutcome.Resolved, initial.Outcome);
        var adminId = initial.Resolution!.AdminId;
        var merchantId = Guid.Parse("eeeeeeee-0000-4000-8000-000000000001");
        await database.ExecuteAsync(
            """
            DECLARE @roleId uniqueidentifier = (SELECT TOP (1) Id FROM iam.Roles ORDER BY Id);
            INSERT admin.RoleAssignments (Id, AdminUserId, RoleId, AssignedById, AssignedAt)
            VALUES (NEWID(), @adminId, @roleId, @adminId, @assignedAt);
            INSERT admin.MerchantAccess (Id, AdminUserId, MerchantId, AssignedByAdminId, AssignedAt)
            VALUES (NEWID(), @adminId, @merchantId, @adminId, @assignedAt);
            """,
            ("@adminId", adminId), ("@merchantId", merchantId),
            ("@assignedAt", FirstLogin));

        string? preservedBefore;
        await using (var verify = await database.OpenAsync())
            preservedBefore = await PreservedStateAsync(verify, adminId);

        var refreshed = await database.ResolveAsync(
            ObjectId, "changed@example.test", "E002", "corr-refresh", SecondLogin);

        Assert.Equal(ResolveOutcome.Resolved, refreshed.Outcome);
        Assert.Equal(adminId, refreshed.Resolution!.AdminId);
        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal("E002|ชื่อใหม่|นามสกุลใหม่|3|0|" + SecondLogin.ToString("yyyy-MM-dd HH:mm:ss"),
                await database.ProfileRowAsync(verify, adminId));
            Assert.Equal(preservedBefore, await PreservedStateAsync(verify, adminId));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.UserAudits WHERE TargetAdminId = @id AND Action = N'employee-bind';",
                ("@id", adminId))));
            Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.UserAudits WHERE TargetAdminId = @id AND Action = N'employee-profile-sync';",
                ("@id", adminId))));
        }
    }

    [Fact]
    public async Task Profile_denials_leave_no_jit_or_success_audit_and_do_not_change_an_existing_tuple_or_profile()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var missingObjectId = Guid.Parse("33333333-3333-4333-8333-333333333333");

        var missing = await database.ResolveAsync(
            missingObjectId, "optional@example.com", "ZTEST-NONE", "corr-missing", FirstLogin);

        Assert.Equal(ResolveOutcome.EmployeeProfileMissing, missing.Outcome);
        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal(0, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.Users WHERE TenantId = @tenant AND Subject = @subject;",
                ("@tenant", TenantId), ("@subject", missingObjectId.ToString("D")))));
            Assert.Equal(0, Convert.ToInt32(await ScalarAsync(verify, "SELECT COUNT(*) FROM admin.UserAudits;")));
        }

        var existingObjectId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        await database.SeedHrAsync("ZTEST-T2", "เดิม", "คงไว้");
        var seeded = await database.ResolveAsync(
            existingObjectId, "stored@example.com", "ZTEST-T2", "corr-seed", FirstLogin);
        Assert.Equal(ResolveOutcome.Resolved, seeded.Outcome);
        string? identityBefore;
        string? profileBefore;
        await using (var verify = await database.OpenAsync())
        {
            identityBefore = await database.IdentityRowAsync(verify, seeded.Resolution!.AdminId);
            profileBefore = await database.ProfileRowAsync(verify, seeded.Resolution.AdminId);
        }

        await database.ExecuteAsync(
            "UPDATE dbo.VibEmp SET FirstNameTh = N' ' WHERE EmpCode = N'ZTEST-T2';");
        var denied = await database.ResolveAsync(
            existingObjectId, "changed@example.com", "ZTEST-T2", "corr-denied", SecondLogin);

        Assert.Equal(ResolveOutcome.EmployeeProfileInvalid, denied.Outcome);
        await using (var verify = await database.OpenAsync())
        {
            Assert.Equal(identityBefore, await database.IdentityRowAsync(verify, seeded.Resolution.AdminId));
            Assert.Equal(profileBefore, await database.ProfileRowAsync(verify, seeded.Resolution.AdminId));
            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(verify,
                "SELECT COUNT(*) FROM admin.UserAudits WHERE TargetAdminId = @id;",
                ("@id", seeded.Resolution.AdminId))));
        }
    }

    [Fact]
    public async Task Same_tuple_concurrent_jit_returns_one_admin_and_one_jit_audit()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var objectId = Guid.Parse("55555555-5555-4555-8555-555555555555");
        await database.SeedHrAsync("ZTEST-C1", "พร้อม", "กัน");

        var (first, second) = await RunTogetherAsync(
            () => database.ResolveAsync(objectId, null, "ZTEST-C1", "corr-a", FirstLogin),
            () => database.ResolveAsync(objectId, "same@example.com", "ZTEST-C1", "corr-b", FirstLogin));

        Assert.Equal(ResolveOutcome.Resolved, first.Outcome);
        Assert.Equal(ResolveOutcome.Resolved, second.Outcome);
        Assert.Equal(first.Resolution!.AdminId, second.Resolution!.AdminId);
        await using var verify = await database.OpenAsync();
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.Users WHERE TenantId = @tenant AND Subject = @subject;",
            ("@tenant", TenantId), ("@subject", objectId.ToString("D")))));
        Assert.Equal("ZTEST-C1", Convert.ToString(await ScalarAsync(verify,
            "SELECT EmployeeId FROM admin.Users WHERE Id = @id;", ("@id", first.Resolution.AdminId))));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'jit-provision' AND TargetAdminId = @id;",
            ("@id", first.Resolution.AdminId))));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'employee-bind' AND TargetAdminId = @id;",
            ("@id", first.Resolution.AdminId))));
    }

    [Fact]
    public async Task Different_tuples_with_the_same_contact_create_independent_admins()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var firstObjectId = Guid.Parse("66666666-6666-4666-8666-666666666666");
        var secondObjectId = Guid.Parse("77777777-7777-4777-8777-777777777777");

        var (first, second) = await RunTogetherAsync(
            () => database.ResolveAsync(firstObjectId, "shared@example.com", null, "corr-a", FirstLogin),
            () => database.ResolveAsync(secondObjectId, "shared@example.com", null, "corr-b", FirstLogin));

        Assert.Equal(ResolveOutcome.Resolved, first.Outcome);
        Assert.Equal(ResolveOutcome.Resolved, second.Outcome);
        Assert.NotEqual(first.Resolution!.AdminId, second.Resolution!.AdminId);
        await using var verify = await database.OpenAsync();
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.Users WHERE TenantId = @tenant AND Email = N'shared@example.com';",
            ("@tenant", TenantId))));
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'jit-provision';")));
    }

    [Fact]
    public async Task Direct_unique_winner_is_resolved_by_the_fresh_exact_recovery_context()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var objectId = Guid.Parse("88888888-8888-4888-8888-888888888888");
        var winnerId = Guid.Parse("99999999-9999-4999-8999-999999999999");
        var inserted = 0;

        var result = await database.ResolveAsync(
            objectId,
            "loser-contact@example.com",
            employeeId: null,
            "corr-loser",
            FirstLogin,
            afterIdentityLookup: async account =>
            {
                if (account is null && Interlocked.Exchange(ref inserted, 1) == 0)
                    await database.InsertDirectWinnerAsync(
                        winnerId, objectId, "winner-contact@example.com", "corr-winner", FirstLogin);
            });

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(winnerId, result.Resolution!.AdminId);
        Assert.Equal("winner-contact@example.com", result.Resolution.Email);
        await using var verify = await database.OpenAsync();
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.Users WHERE TenantId = @tenant AND Subject = @subject;",
            ("@tenant", TenantId), ("@subject", objectId.ToString("D")))));
        Assert.Equal("corr-winner", Convert.ToString(await ScalarAsync(verify,
            "SELECT CorrelationId FROM admin.UserAudits WHERE Action = N'jit-provision' AND TargetAdminId = @id;",
            ("@id", winnerId))));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE Action = N'jit-provision';")));
    }

    [Fact]
    public async Task Employee_id_unique_race_retries_once_then_returns_global_profile_conflict()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        await database.SeedHrAsync("ZTEST-T4", "a", "b");
        var winnerId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
        var winnerObjectId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");
        var loserObjectId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000003");
        await database.SeedMicrosoftAsync(winnerId, winnerObjectId, "winner@example.com");

        var raced = 0;
        var result = await database.ResolveAsync(
            loserObjectId,
            "loser@example.com",
            "ZTEST-T4",
            "corr-race",
            FirstLogin,
            afterEmployeeCheck: async () =>
            {
                if (raced++ == 0)
                {
                    await database.ExecuteAsync(
                        "UPDATE admin.Users SET EmployeeId = N'ZTEST-T4' WHERE Id = @id;", ("@id", winnerId));
                }
            });

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(ResolveResult.EmployeeTakenReason, result.DenialReason);
        Assert.Equal(2, raced);
        await using var verify = await database.OpenAsync();
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(verify,
            "SELECT COUNT(*) FROM admin.Users WHERE TenantId = @tenant AND Subject = @subject;",
            ("@tenant", TenantId), ("@subject", loserObjectId.ToString("D")))));
        Assert.Equal("ZTEST-T4", Convert.ToString(await ScalarAsync(verify,
            "SELECT EmployeeId FROM admin.Users WHERE Id = @id;", ("@id", winnerId))));
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(verify, "SELECT COUNT(*) FROM admin.UserAudits;")));
    }

    [Fact]
    public async Task Changed_employee_id_unique_race_rolls_back_existing_profile_as_employee_taken()
    {
        await using var database = await ScratchDatabase.CreateAsync();
        var loserObjectId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000001");
        var winnerId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
        var winnerObjectId = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000003");
        await database.SeedHrAsync("E001", "ชื่อเดิม", "นามสกุลเดิม");
        await database.SeedHrAsync("E002", "ชื่อใหม่", "นามสกุลใหม่");
        var initial = await database.ResolveAsync(
            loserObjectId, "loser@example.test", "E001", "corr-initial", FirstLogin);
        Assert.Equal(ResolveOutcome.Resolved, initial.Outcome);
        var loserId = initial.Resolution!.AdminId;
        await database.SeedMicrosoftAsync(winnerId, winnerObjectId, "winner@example.test");
        string? profileBefore;
        await using (var verify = await database.OpenAsync())
            profileBefore = await database.ProfileRowAsync(verify, loserId);

        var raced = 0;
        var result = await database.ResolveAsync(
            loserObjectId,
            "loser@example.test",
            "E002",
            "corr-race-existing",
            SecondLogin,
            afterEmployeeCheck: async () =>
            {
                if (raced++ == 0)
                    await database.ExecuteAsync(
                        "UPDATE admin.Users SET EmployeeId = N'E002' WHERE Id = @id;", ("@id", winnerId));
            });

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(ResolveResult.EmployeeTakenReason, result.DenialReason);
        Assert.Equal(2, raced);
        await using var final = await database.OpenAsync();
        Assert.Equal(profileBefore, await database.ProfileRowAsync(final, loserId));
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(final,
            "SELECT COUNT(*) FROM admin.UserAudits WHERE TargetAdminId = @id AND Action = N'employee-profile-sync';",
            ("@id", loserId))));
        Assert.Equal("E002", Convert.ToString(await ScalarAsync(final,
            "SELECT EmployeeId FROM admin.Users WHERE Id = @id;", ("@id", winnerId))));
    }

    private static async Task<(T First, T Second)> RunTogetherAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<T> WaitAndRunAsync(Func<Task<T>> operation)
        {
            await gate.Task;
            return await operation();
        }

        var firstTask = WaitAndRunAsync(first);
        var secondTask = WaitAndRunAsync(second);
        gate.SetResult();
        return (await firstTask, await secondTask);
    }

    private static async Task<string?> PreservedStateAsync(SqlConnection connection, Guid adminId) =>
        Convert.ToString(await ScalarAsync(
            connection,
            """
            SELECT CONCAT(
                u.Id, '|', u.Tier, '|', u.AuthorizationVersion, '|',
                (SELECT COUNT(*) FROM admin.RoleAssignments r WHERE r.AdminUserId = u.Id), '|',
                (SELECT COUNT(*) FROM admin.MerchantAccess m WHERE m.AdminUserId = u.Id))
            FROM admin.Users u WHERE u.Id = @id;
            """,
            ("@id", adminId)));

    private static async Task<object?> ScalarAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var scalar = await command.ExecuteScalarAsync();
        return scalar is DBNull ? null : scalar;
    }

    private sealed class HookedRepository(
        IUserRepository inner,
        Func<User?, Task>? afterIdentityLookup,
        Func<Task>? afterEmployeeCheck) : IUserRepository
    {
        public void Add(User account) => inner.Add(account);
        public void AddAssignment(MerchantAccess assignment) => inner.AddAssignment(assignment);
        public void RemoveAssignment(MerchantAccess assignment) => inner.RemoveAssignment(assignment);
        public Task AcquireIdentityMutationLockAsync(CancellationToken ct) => inner.AcquireIdentityMutationLockAsync(ct);

        public async Task<User?> GetByMicrosoftIdentityAsync(Guid tenantId, Guid objectId, CancellationToken ct)
        {
            var result = await inner.GetByMicrosoftIdentityAsync(tenantId, objectId, ct);
            if (afterIdentityLookup is not null)
                await afterIdentityLookup(result);
            return result;
        }

        public Task<User?> GetByIdentityAsync(ProviderIdentity identity, CancellationToken ct) =>
            inner.GetByIdentityAsync(identity, ct);
        public Task<User?> GetByEmailAsync(string email, CancellationToken ct) => inner.GetByEmailAsync(email, ct);
        public Task<User?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

        public async Task<User?> GetByEmployeeIdAsync(string employeeId, Guid exceptAdminId, CancellationToken ct)
        {
            var result = await inner.GetByEmployeeIdAsync(employeeId, exceptAdminId, ct);
            if (afterEmployeeCheck is not null)
                await afterEmployeeCheck();
            return result;
        }

        public Task VerifyActiveSuperAsync(Guid callerId, long expectedAuthorizationVersion, CancellationToken ct) =>
            inner.VerifyActiveSuperAsync(callerId, expectedAuthorizationVersion, ct);
        public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => inner.ExistsAsync(id, ct);
        public Task<IReadOnlySet<Guid>> ListAssignedMerchantIdsAsync(Guid adminAccountId, CancellationToken ct) =>
            inner.ListAssignedMerchantIdsAsync(adminAccountId, ct);
        public Task<MerchantAccess?> GetAssignmentAsync(Guid adminAccountId, Guid merchantId, CancellationToken ct) =>
            inner.GetAssignmentAsync(adminAccountId, merchantId, ct);
        public Task<PagedResult<UserListItem>> ListAsync(PagedQuery query, CancellationToken ct) =>
            inner.ListAsync(query, ct);
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    private sealed class ScratchContextFactory(ScratchDatabase database)
        : IDbContextFactory<ControlPlaneDbContext>
    {
        public ControlPlaneDbContext CreateDbContext() => database.RuntimeContext(FirstLogin);
        public Task<ControlPlaneDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    private sealed class ScratchDatabase : IAsyncDisposable
    {
        private const string Prefix = "pol_t0profile_tx_it_";

        private ScratchDatabase(string name) => Name = name;

        public string Name { get; }

        public static async Task<ScratchDatabase> CreateAsync()
        {
            var database = new ScratchDatabase(Prefix + Guid.NewGuid().ToString("N"));
            await using (var master = await database.OpenAsync("master"))
            {
                await ExecuteAsync(master, $"EXEC(N'CREATE DATABASE [{database.Name}] COLLATE Thai_100_CI_AS');");
                await ExecuteAsync(master, $"ALTER DATABASE [{database.Name}] SET COMPATIBILITY_LEVEL = 170;");
            }

            await database.ExecuteAsync("CREATE USER pol_app WITHOUT LOGIN;");
            await database.MigrateAsync();
            await database.ExecuteAsync(
                """
                CREATE TABLE dbo.VibEmp (
                    EmpCode nvarchar(50) NULL,
                    FirstNameTh nvarchar(500) NULL,
                    LastNameTh nvarchar(500) NULL);
                """);

            var output = new StringWriter();
            var code = await WorkforceIdentityMigration.RunAsync(
                ConnectionString(database.Name),
                new WorkforceIdentityMigrationInputs(null, null, null, null, null, null),
                output,
                CancellationToken.None);
            if (code != 0)
                throw new InvalidOperationException("Scratch identity migration initialization failed.");

            await using (var context = database.RuntimeContext(FirstLogin))
            {
                var telemetry = NoOpSecurityTelemetry.Instance;
                var store = new WorkforceTenantBindingStore(
                    context,
                    new ControlPlaneUnitOfWork(context, telemetry),
                    new GovernanceSqlLockManager(context));
                await store.EnsureAsync(TenantId, CancellationToken.None);
            }

            return database;
        }

        public Task SeedHrAsync(string employeeId, string first, string last) =>
            ExecuteAsync(
                """
                INSERT dbo.VibEmp (EmpCode, FirstNameTh, LastNameTh)
                VALUES (@employeeId, @first, @last);
                """,
                ("@employeeId", employeeId), ("@first", first), ("@last", last));

        public Task SeedMicrosoftAsync(Guid adminId, Guid objectId, string? email) =>
            ExecuteAsync(
                """
                INSERT admin.Users (
                    Id, Provider, TenantId, Subject, Email, Tier, Status,
                    AuthorizationVersion, Version, CreatedAt)
                VALUES (
                    @id, N'microsoft', @tenantId, @subject, @email, 1, 1, 0, 1, SYSUTCDATETIME());
                """,
                ("@id", adminId), ("@tenantId", TenantId), ("@subject", objectId.ToString("D")), ("@email", email));

        public Task InsertDirectWinnerAsync(
            Guid adminId, Guid objectId, string? email, string correlationId, DateTime occurredAt) =>
            ExecuteAsync(
                """
                BEGIN TRANSACTION;
                INSERT admin.Users (
                    Id, Provider, TenantId, Subject, Email, Tier, Status,
                    AuthorizationVersion, Version, CreatedAt)
                VALUES (
                    @id, N'microsoft', @tenantId, @subject, @email, 1, 1, 0, 1, @occurredAt);
                INSERT admin.UserAudits (
                    Id, Action, ActorType, ActorId, TargetAdminId, MerchantId, TargetRoleId,
                    CorrelationId, OccurredAt)
                VALUES (
                    NEWID(), N'jit-provision', N'admin', @id, @id, NULL, NULL,
                    @correlationId, @occurredAt);
                COMMIT TRANSACTION;
                """,
                ("@id", adminId), ("@tenantId", TenantId), ("@subject", objectId.ToString("D")),
                ("@email", email), ("@correlationId", correlationId), ("@occurredAt", occurredAt));

        public async Task<string?> IdentityRowAsync(SqlConnection connection, Guid adminId) =>
            Convert.ToString(await ScalarAsync(
                connection,
                """
                SELECT CONCAT(
                    Provider, '|', LOWER(CONVERT(nvarchar(36), TenantId)), '|', Subject, '|',
                    COALESCE(Email, N'<null>'), '|', Tier, '|', Status, '|', Version, '|', AuthorizationVersion)
                FROM admin.Users WHERE Id = @id;
                """, ("@id", adminId)));

        public async Task<string?> ProfileRowAsync(SqlConnection connection, Guid adminId) =>
            Convert.ToString(await ScalarAsync(
                connection,
                """
                SELECT CONCAT(
                    EmployeeId, '|', FirstName, '|', LastName, '|',
                    Version, '|', AuthorizationVersion, '|', CONVERT(nvarchar(19), UpdatedAt, 120))
                FROM admin.Users WHERE Id = @id;
                """, ("@id", adminId)));

        public async Task<ResolveResult> ResolveAsync(
            Guid objectId,
            string? email,
            string? employeeId,
            string correlationId,
            DateTime now,
            Func<User?, Task>? afterIdentityLookup = null,
            Func<Task>? afterEmployeeCheck = null)
        {
            await using var context = RuntimeContext(now);
            var telemetry = NoOpSecurityTelemetry.Instance;
            IUserRepository admins = new UserRepository(
                context,
                NullLogger<UserRepository>.Instance,
                telemetry,
                new GovernanceSqlLockManager(context));
            if (afterIdentityLookup is not null || afterEmployeeCheck is not null)
                admins = new HookedRepository(admins, afterIdentityLookup, afterEmployeeCheck);

            var recovery = new ControlPlaneIdentityRecoveryReader(
                new ScratchContextFactory(this), NullLoggerFactory.Instance, telemetry);
            var handler = new ResolveMicrosoftAdminHandler(
                admins,
                new RoleRepository(context),
                new AuditWriter(context),
                recovery,
                new EmployeeProfileReader(context, NullLogger<EmployeeProfileReader>.Instance),
                new ControlPlaneUnitOfWork(context, telemetry),
                new FixedClock(now));
            return await handler.Handle(
                new ResolveMicrosoftAdminCommand(
                    TenantId, objectId, email, employeeId, correlationId),
                CancellationToken.None);
        }

        public async Task ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var connection = await OpenAsync();
            await ExecuteAsync(connection, sql, parameters);
        }

        private static async Task ExecuteAsync(
            SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<SqlConnection> OpenAsync(string? database = null)
        {
            var connection = new SqlConnection(ConnectionString(database ?? Name));
            await connection.OpenAsync();
            return connection;
        }

        public ControlPlaneDbContext RuntimeContext(DateTime now)
        {
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlServer(ConnectionString(Name), sql => sql.UseCompatibilityLevel(170))
                .AddInterceptors(new UserUpdatedAtInterceptor(new FixedClock(now)))
                .Options;
            return new ControlPlaneDbContext(
                options, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);
        }

        private async Task MigrateAsync(string? target = null)
        {
            // EnableServiceProviderCaching(false): EF's model cache keys on the CONTEXT TYPE, not on
            // ModuleAssemblies — without it, this shares a cached model with every other PolDbContext test
            // in the process and whichever runs first wins, causing a spurious PendingModelChangesWarning
            // here (see EntitySchemaMappingTests for the same guard).
            var options = new DbContextOptionsBuilder<PolDbContext>()
                .UseSqlServer(ConnectionString(Name), sql => sql.UseCompatibilityLevel(170))
                .EnableServiceProviderCaching(false)
                .Options;
            await using var context = new PolDbContext(options, ModuleAssemblies());
            await context.GetService<IMigrator>().MigrateAsync(target);
        }

        public async ValueTask DisposeAsync()
        {
            if (!Name.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(Name[Prefix.Length..], "N", out _))
            {
                throw new InvalidOperationException("Scratch database name is invalid.");
            }

            await using var master = await OpenAsync("master");
            await ExecuteAsync(
                master,
                $"IF DB_ID(N'{Name}') IS NOT NULL BEGIN "
                + $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}]; END");
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

        private static string ConnectionString(string database) => new SqlConnectionStringBuilder
        {
            DataSource = Environment.GetEnvironmentVariable("POL_SQL_SERVER") ?? "localhost,11433",
            InitialCatalog = database,
            UserID = "sa",
            Password = Environment.GetEnvironmentVariable("POL_SA_PASSWORD")
                ?? throw new InvalidOperationException("Integration tests need env var 'POL_SA_PASSWORD'."),
            Encrypt = true,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString;
    }
}
