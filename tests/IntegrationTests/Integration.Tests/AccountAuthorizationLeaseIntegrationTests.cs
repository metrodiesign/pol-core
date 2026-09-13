using Accounts.Domain;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.IdentityAccess;

namespace Integration.Tests;

[Collection("IdentityAccessSql")]
[Trait("Capability", "IdentityAccess")]
[Trait("Category", "Integration")]
public sealed class AccountAuthorizationLeaseIntegrationTests
{
    [Fact]
    [Trait("Requirement", "REQ-3.11")]
    [Trait("Requirement", "REQ-2.10")]
    public async Task Sql_account_lease_rejects_revoke_committed_before_business_write()
    {
        var accountId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.ExecAsync(setup,
            "INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt) VALUES (@id, 1, N'Lease test', 1, 0, @now, @now);",
            ("@id", accountId), ("@now", now));

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
            .Options;
        try
        {
            await using var leaseDb = new ControlPlaneDbContext(options, AllowAll.Instance, NoOpSecurityTelemetry.Instance);
            await using var transaction = await leaseDb.Database.BeginTransactionAsync();
            var lease = new AccountAuthorizationLease(leaseDb, NoOpSecurityTelemetry.Instance);
            await lease.VerifyAsync(accountId, 0, default);
            var account = await leaseDb.Accounts.SingleAsync(x => x.Id == accountId);
            account.Rename("Lease business write", now);

            await IntegrationDb.ExecAsync(setup,
                "UPDATE acct.Accounts SET AuthorizationVersion=1, UpdatedAt=@now WHERE Id=@id;",
                ("@id", accountId), ("@now", now.AddSeconds(1)));

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => leaseDb.SaveChangesAsync());
            await transaction.RollbackAsync();
        }
        finally
        {
            await IntegrationDb.ExecAsync(setup,
                "DELETE FROM acct.Accounts WHERE Id=@id;",
                ("@id", accountId));
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-3.11")]
    public async Task Sql_business_commit_precedes_a_concurrent_revoke_waiting_on_the_same_row()
    {
        var accountId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.ExecAsync(setup,
            "INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt) VALUES (@id, 1, N'Inverse lease test', 1, 0, @now, @now);",
            ("@id", accountId), ("@now", now));

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
            .Options;
        try
        {
            await using var businessDb = new ControlPlaneDbContext(options, AllowAll.Instance, NoOpSecurityTelemetry.Instance);
            await using var transaction = await businessDb.Database.BeginTransactionAsync();
            await new AccountAuthorizationLease(businessDb, NoOpSecurityTelemetry.Instance)
                .VerifyAsync(accountId, 0, default);
            var account = await businessDb.Accounts.SingleAsync(x => x.Id == accountId);
            account.Rename("business committed first", now);
            await businessDb.SaveChangesAsync();

            var revokeTask = Task.Run(() => IntegrationDb.ExecAsync(setup,
                "UPDATE acct.Accounts SET AuthorizationVersion=1, UpdatedAt=@now WHERE Id=@id;",
                ("@id", accountId), ("@now", now.AddSeconds(1))));
            var completedBeforeCommit = await Task.WhenAny(revokeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.NotSame(revokeTask, completedBeforeCommit);

            await transaction.CommitAsync();
            await revokeTask.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(setup,
                "SELECT AuthorizationVersion FROM acct.Accounts WHERE Id=@id;",
                ("@id", accountId))));
        }
        finally
        {
            await IntegrationDb.ExecAsync(setup,
                "DELETE FROM acct.Accounts WHERE Id=@id;",
                ("@id", accountId));
        }
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
