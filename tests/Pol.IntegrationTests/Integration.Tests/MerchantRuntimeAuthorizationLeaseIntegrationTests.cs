using BuildingBlocks.Application;
using Governance.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Payments;

namespace Integration.Tests;

[Collection("IdentityAccessSql")]
[Trait("Capability", "MigrationReadiness")]
[Trait("Category", "Integration")]
public sealed class MerchantRuntimeAuthorizationLeaseIntegrationTests
{
    [Fact]
    [Trait("Requirement", "REQ-3.11")]
    public async Task Sql_revoke_committed_before_business_lease_denies_without_business_record()
    {
        var actorId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var setup = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await SeedAdminAsync(setup, actorId, now);

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
            .Options;
        try
        {
            await IntegrationDb.ExecAsync(setup,
                "UPDATE admin.Users SET AuthorizationVersion=1 WHERE Id=@id;",
                ("@id", actorId));

            await using var business = new ControlPlaneDbContext(
                options, AllowAll.Instance, NoOpSecurityTelemetry.Instance);
            await using var transaction = await business.Database.BeginTransactionAsync();
            var error = await Assert.ThrowsAsync<AccessDeniedException>(() =>
                new MerchantRuntimeAuthorizationLease(business, NoOpSecurityTelemetry.Instance)
                    .VerifyAsync(Access(actorId, 0), default));

            Assert.Equal("authorization_stale", error.Code);
            Assert.Empty(await business.OperationRecords
                .Where(x => x.ActorId == actorId && x.Operation == "lease-ordering")
                .ToListAsync());
            await transaction.RollbackAsync();
        }
        finally
        {
            await CleanupAsync(setup, actorId);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-3.11")]
    public async Task Sql_business_lease_holds_row_lock_until_commit_then_revoke_completes()
    {
        var actorId = Guid.NewGuid();
        var merchantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var setup = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await SeedAdminAsync(setup, actorId, now);

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
            .Options;
        try
        {
            await using var business = new ControlPlaneDbContext(
                options, AllowAll.Instance, NoOpSecurityTelemetry.Instance);
            await using var transaction = await business.Database.BeginTransactionAsync();
            await new MerchantRuntimeAuthorizationLease(business, NoOpSecurityTelemetry.Instance)
                .VerifyAsync(Access(actorId, 0), default);
            business.OperationRecords.Add(OperationRecord.Create(
                actorId, "lease-ordering", actorId.ToString("N"), new string('c', 64),
                GovernanceScopeKind.Merchant, merchantId, now, now.AddHours(24)));
            await business.SaveChangesAsync();

            var revokeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var revokeTask = Task.Run(async () =>
            {
                revokeStarted.SetResult(true);
                return await IntegrationDb.ExecAsync(setup,
                    "UPDATE admin.Users SET AuthorizationVersion=1 WHERE Id=@id;",
                    ("@id", actorId));
            });
            await revokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var completedBeforeCommit = await Task.WhenAny(revokeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.NotSame(revokeTask, completedBeforeCommit);

            await transaction.CommitAsync();
            await revokeTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(setup,
                "SELECT AuthorizationVersion FROM admin.Users WHERE Id=@id;",
                ("@id", actorId))));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(setup,
                "SELECT COUNT_BIG(*) FROM admin.OperationRecords WHERE ActorId=@id AND Operation=N'lease-ordering';",
                ("@id", actorId))));
        }
        finally
        {
            await CleanupAsync(setup, actorId);
        }
    }

    private static async Task SeedAdminAsync(SqlConnection connection, Guid actorId, DateTime now) =>
        await IntegrationDb.ExecAsync(connection,
            """
            INSERT admin.Users
                (Id, Provider, Subject, Email, Tier, Status, AuthorizationVersion, Version, CreatedAt)
            VALUES (@id, N'microsoft', @subject, @email, 2, 1, 0, 1, @now);
            """,
            ("@id", actorId), ("@subject", $"lease-{actorId:N}"),
            ("@email", $"lease-{actorId:N}@example.test"), ("@now", now));

    private static async Task CleanupAsync(SqlConnection connection, Guid actorId) =>
        await IntegrationDb.ExecAsync(connection,
            """
            DELETE FROM admin.OperationRecords WHERE ActorId=@id;
            DELETE FROM admin.Users WHERE Id=@id;
            """,
            ("@id", actorId));

    private static AdminPaymentsAccess Access(Guid actorId, long version) =>
        new(actorId, version, true, new HashSet<Guid>());

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
