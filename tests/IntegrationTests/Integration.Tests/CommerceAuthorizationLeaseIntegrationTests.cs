using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Persistence.ControlPlane;
using Persistence.ControlPlane.IdentityAccess;
using Persistence.ControlPlane.Iam;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Authorization;

namespace Integration.Tests;

[Collection("IdentityAccessSql")]
[Trait("Capability", "ApiOperations")]
[Trait("Category", "Integration")]
public sealed class CommerceAuthorizationLeaseIntegrationTests
{
    [Fact]
    [Trait("Requirement", "REQ-3.11")]
    public async Task Sql_business_lease_holds_identity_account_lock_until_commit_then_revoke_completes()
    {
        var database = $"PolPr253LeaseRace{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");

        await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
        {
            await IntegrationDb.InsertMerchantAsync(seed, merchantId, $"lease-race-{runTag}"[..20]);
            await IntegrationDb.ExecAsync(seed, """
                INSERT acct.Accounts
                    (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Lease Race Employee', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess
                    (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 1, 1, 1);
                INSERT iam.Roles
                    (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @code, N'Lease Race Role', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@account", accountId), ("@access", accessId), ("@merchant", merchantId),
                ("@role", roleId), ("@code", $"lease-race-role-{runTag}"[..24]));
        }

        try
        {
            var options = new DbContextOptionsBuilder<CommerceDbContext>()
                .UseSqlServer(IntegrationDb.AppConnFor(database), sql => sql.UseCompatibilityLevel(170))
                .Options;
            await using var commerce = new CommerceDbContext(
                options,
                new IntegrationActor(merchantId, accountId),
                AllowAll.Instance,
                NoOpSecurityTelemetry.Instance);
            await using var transaction = await commerce.Database.BeginTransactionAsync();
            var proof = new CommerceAuthorizationProof(
                accountId, 0, merchantId, null, "payment.create", null);
            await new CommerceAuthorizationLease(commerce, NoOpSecurityTelemetry.Instance)
                .VerifyAsync(proof, default);

            var revokeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var revokeTask = Task.Run(async () =>
            {
                await using var revoke = await IntegrationDb.OpenAsync(IntegrationDb.AppConnFor(database));
                revokeStarted.SetResult(true);
                await IntegrationDb.ExecAsync(revoke,
                    "UPDATE acct.Accounts SET AuthorizationVersion=1 WHERE Id=@account;",
                    ("@account", accountId));
            });
            await revokeStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var completedBeforeCommit = await Task.WhenAny(revokeTask, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.NotSame(revokeTask, completedBeforeCommit);

            await transaction.CommitAsync();
            await revokeTask.WaitAsync(TimeSpan.FromSeconds(3));

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify,
                "SELECT AuthorizationVersion FROM acct.Accounts WHERE Id=@account;",
                ("@account", accountId))));
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-4.6")]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Sql_role_invalidation_waits_for_commerce_lease_then_bumps_account_and_stales_old_proof()
    {
        var database = $"PolPr253RoleRace{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow;

        await using (var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database)))
        {
            await IntegrationDb.InsertMerchantAsync(seed, merchantId, $"role-race-{runTag}"[..20]);
            await IntegrationDb.ExecAsync(seed, """
                INSERT acct.Accounts
                    (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Role Race Employee', 1, 0, @now, @now);
                INSERT access.MerchantAccess
                    (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 1, 1, 1);
                INSERT iam.Roles
                    (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @code, N'Role Race Role', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@account", accountId), ("@access", accessId), ("@merchant", merchantId),
                ("@role", roleId), ("@code", $"role-race-{runTag}"[..24]), ("@now", now));
        }

        try
        {
            var commerceOptions = new DbContextOptionsBuilder<CommerceDbContext>()
                .UseSqlServer(IntegrationDb.AppConnFor(database), sql => sql.UseCompatibilityLevel(170))
                .Options;
            await using var commerce = new CommerceDbContext(
                commerceOptions,
                new IntegrationActor(merchantId, accountId),
                AllowAll.Instance,
                NoOpSecurityTelemetry.Instance);
            await using var commerceTransaction = await commerce.Database.BeginTransactionAsync();
            var proof = new CommerceAuthorizationProof(
                accountId, 0, merchantId, null, "payment.create", null);
            await new CommerceAuthorizationLease(commerce, NoOpSecurityTelemetry.Instance)
                .VerifyAsync(proof, default);

            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var roleUpdate = Task.Run(async () =>
            {
                var controlOptions = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                    .UseSqlServer(IntegrationDb.AppConnFor(database), sql => sql.UseCompatibilityLevel(170))
                    .Options;
                await using var control = new ControlPlaneDbContext(
                    controlOptions, AllowAll.Instance, NoOpSecurityTelemetry.Instance);
                await using var roleTransaction = await control.Database.BeginTransactionAsync();
                var role = await control.Roles.SingleAsync(x => x.Id == roleId);
                role.Deactivate();
                role.BumpVersion();
                started.SetResult(true);
                await new RoleAuthorizationInvalidator(control, new FixedClock(now))
                    .InvalidateAssignedAccountsAsync(roleId, default);
                await control.SaveChangesAsync();
                await roleTransaction.CommitAsync();
            });

            await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var completedBeforeCommerceCommit = await Task.WhenAny(
                roleUpdate, Task.Delay(TimeSpan.FromSeconds(3)));
            Assert.NotSame(roleUpdate, completedBeforeCommerceCommit);

            await commerceTransaction.CommitAsync();
            await roleUpdate.WaitAsync(TimeSpan.FromSeconds(3));

            await using var verify = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));
            Assert.Equal(1, Convert.ToInt64(await IntegrationDb.ScalarAsync(verify,
                "SELECT AuthorizationVersion FROM acct.Accounts WHERE Id=@account;", ("@account", accountId))));
            Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(verify,
                "SELECT Status FROM iam.Roles WHERE Id=@role;", ("@role", roleId))));

            await using var staleCommerce = new CommerceDbContext(
                new DbContextOptionsBuilder<CommerceDbContext>()
                    .UseSqlServer(IntegrationDb.AppConnFor(database), sql => sql.UseCompatibilityLevel(170))
                    .Options,
                new IntegrationActor(merchantId, accountId), AllowAll.Instance, NoOpSecurityTelemetry.Instance);
            await using var staleTransaction = await staleCommerce.Database.BeginTransactionAsync();
            var stale = await Assert.ThrowsAsync<AccessDeniedException>(() =>
                new CommerceAuthorizationLease(staleCommerce, NoOpSecurityTelemetry.Instance)
                    .VerifyAsync(proof, default));
            Assert.Equal("authorization_stale", stale.Code);
            await staleTransaction.RollbackAsync();
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private sealed class IntegrationActor(Guid merchantId, Guid? userId) : IActorContext
    {
        public Guid MerchantId { get; } = merchantId;
        public Guid? UserId { get; } = userId;
        public bool HasActor => true;
    }

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
    }
}
