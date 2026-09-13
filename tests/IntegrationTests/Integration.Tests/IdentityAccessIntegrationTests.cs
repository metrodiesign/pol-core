using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.IdentityAccess;

namespace Integration.Tests;

[CollectionDefinition("IdentityAccessSql", DisableParallelization = true)]
public sealed class IdentityAccessSqlCollection;

[Collection("IdentityAccessSql")]
[Trait("Capability", "IdentityAccess")]
[Trait("Category", "Integration")]
public sealed class IdentityAccessIntegrationTests
{
    [Fact]
    [Trait("Requirement", "REQ-2.4")]
    public async Task Sql_server_serializable_jit_race_creates_one_account_login_and_employee()
    {
        var identity = new VerifiedHumanIdentity(
            ExternalIdentity.Create("microsoft", "task2-tenant", $"jit-{Guid.NewGuid():N}"),
            "https://issuer.task2.test",
            "audience-task2",
            SignatureValidated: true,
            LifetimeValidated: true,
            StateValidated: true,
            NonceValidated: true,
            WorkforceEligible: true,
            Email: "jit@example.test",
            DisplayName: "JIT Employee");

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
            .Options;

        async Task<EmployeeJitResult> ResolveAsync()
        {
            await using var db = new ControlPlaneDbContext(options, AllowAll.Instance, NoOpSecurityTelemetry.Instance);
            var store = new IdentityAccessStore(db, new SystemClock());
            return await store.GetOrCreateAsync(identity, default);
        }

        var results = await Task.WhenAll(ResolveAsync(), ResolveAsync());

        Assert.Equal(results[0].AccountId, results[1].AccountId);
        Assert.Contains(results, result => result.Created);
        Assert.Contains(results, result => !result.Created);

        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
            "SELECT COUNT(*) FROM acct.LoginAccounts WHERE Provider=@provider AND TenantId=@tenant AND ExternalUserId=@external",
            ("@provider", identity.Identity.Provider),
            ("@tenant", identity.Identity.TenantId),
            ("@external", identity.Identity.ExternalUserId))));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
            "SELECT COUNT(*) FROM acct.Employees WHERE AccountId=@account",
            ("@account", results[0].AccountId))));

        await IntegrationDb.ExecAsync(connection,
            "DELETE FROM acct.Employees WHERE AccountId=@account; DELETE FROM acct.LoginAccounts WHERE AccountId=@account; DELETE FROM acct.Accounts WHERE Id=@account;",
            ("@account", results[0].AccountId));
    }

    [Fact]
    [Trait("Requirement", "REQ-2.2")]
    [Trait("Requirement", "REQ-2.8")]
    public async Task Sql_identity_resolution_uses_provider_tenant_external_id_and_replay_jti_is_unique()
    {
        var externalUserId = $"identity-{Guid.NewGuid():N}";
        var firstIdentity = new VerifiedHumanIdentity(
            ExternalIdentity.Create("microsoft", "tenant-a", externalUserId),
            "issuer-a", "audience-a", true, true, true, true, true,
            "first@example.test", "First");
        var changedObservation = firstIdentity with
        {
            Email = "changed@example.test",
            DisplayName = "Changed"
        };
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
            .Options;

        Guid accountId;
        await using (var db = new ControlPlaneDbContext(options, AllowAll.Instance, NoOpSecurityTelemetry.Instance))
        {
            var store = new IdentityAccessStore(db, new SystemClock());
            var first = await store.GetOrCreateAsync(firstIdentity, default);
            var sameTuple = await store.GetOrCreateAsync(changedObservation, default);
            accountId = first.AccountId;
            Assert.Equal(first.AccountId, sameTuple.AccountId);
            Assert.False(sameTuple.Created);
        }

        await using (var db = new ControlPlaneDbContext(options, AllowAll.Instance, NoOpSecurityTelemetry.Instance))
        {
            var store = new IdentityAccessStore(db, new SystemClock());
            var firstClaim = await store.TryClaimAsync("app-sql", "jti-sql", DateTime.UtcNow.AddMinutes(1), default);
            var replayClaim = await store.TryClaimAsync("app-sql", "jti-sql", DateTime.UtcNow.AddMinutes(1), default);
            Assert.True(firstClaim);
            Assert.False(replayClaim);
        }

        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await IntegrationDb.ExecAsync(connection,
            "DELETE FROM acct.Employees WHERE AccountId=@account; DELETE FROM acct.LoginAccounts WHERE AccountId=@account; DELETE FROM acct.Accounts WHERE Id=@account; DELETE FROM oauth.AssertionReplays WHERE ApplicationId=@app AND Jti=@jti;",
            ("@account", accountId), ("@app", "app-sql"), ("@jti", "jti-sql"));
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();

        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
