using System.Text.Json;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using BuildingBlocks.Infrastructure.Persistence;
using Contracts;
using Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Governance;
using Persistence.ControlPlane.IdentityAccess;

namespace Integration.Tests;

[Collection("IdentityAccessSql")]
[Trait("Capability", "Registration")]
public sealed class AgentRegistrationSqlIntegrationTests
{
    private static string Database =>
        Environment.GetEnvironmentVariable("POL_REGISTRATION_DB") ?? "PolRegistrationTask4Test";

    [Fact]
    [Trait("Requirement", "REQ-4.1")]
    [Trait("Requirement", "REQ-4.2")]
    [Trait("Requirement", "REQ-4.3")]
    [Trait("Requirement", "REQ-4.4")]
    public async Task Sql_draft_submit_replay_and_pending_guard_keep_one_case_and_attempt()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var db = NewContext();
            var store = NewStore(db);
            var service = new AgentRegistrationService(store);
            var draft = fixture.Draft("first@example.test", "0812345678");

            var registration = await service.SaveDraftAsync(fixture.Session, draft, null, default);
            Assert.Equal(AgentRegistrationStatus.Draft, registration.Status);
            Assert.Equal(0, registration.CurrentAttemptNo);
            Assert.Equal(0, await db.AgentRegistrationAttempts.CountAsync());
            Assert.Equal(0, await db.Accounts.CountAsync(x => x.Id == registration.Id));

            var submitted = await service.SubmitAsync(fixture.Session, "intent-1", registration.Version, default);
            Assert.False(submitted.Replayed);
            Assert.Equal(1, submitted.Attempt.AttemptNo);
            Assert.Equal(AgentRegistrationAttemptStatus.Pending, submitted.Attempt.Status);
            Assert.True(submitted.Attempt.SubmittedAt > DateTime.MinValue);

            var replay = await service.SubmitAsync(fixture.Session, "intent-1", submitted.Registration.Version, default);
            Assert.True(replay.Replayed);
            Assert.Equal(submitted.Attempt.AttemptId, replay.Attempt.AttemptId);
            Assert.Equal(1, await db.AgentRegistrationAttempts.CountAsync());

            await Assert.ThrowsAsync<ConflictException>(() =>
                service.SubmitAsync(fixture.Session, "different-intent", submitted.Registration.Version, default));
            Assert.Equal(1, await db.AgentRegistrationAttempts.CountAsync());
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-4.8")]
    [Trait("Requirement", "REQ-4.10")]
    [Trait("Requirement", "REQ-4.12")]
    public async Task Sql_reject_keeps_public_reason_then_resubmit_reuses_case_and_outbox_snapshots_contact()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var db = NewContext();
            var service = new AgentRegistrationService(NewStore(db));
            var registration = await service.SaveDraftAsync(
                fixture.Session, fixture.Draft("reject@example.test", "0899991111"), null, default);
            var first = await service.SubmitAsync(fixture.Session, "submit-1", registration.Version, default);

            var rejected = await service.RejectAsync(
                first.Registration.RegistrationId, first.Attempt.AttemptId, Guid.NewGuid(),
                "ข้อมูล Sale ไม่ตรง", "internal reviewer note", "decision-1",
                first.Registration.Version, default);
            Assert.Equal(AgentRegistrationStatus.Rejected, rejected.Registration.Status);
            Assert.Equal("ข้อมูล Sale ไม่ตรง", rejected.Attempt.RejectionReason);
            Assert.Equal(0, await db.Accounts.CountAsync(x => x.AccountType == AccountType.Agent));
            Assert.Equal(1, await db.GovernanceOutboxMessages.CountAsync());

            var eventRow = await db.GovernanceOutboxMessages.SingleAsync();
            Assert.Equal(AgentRegistrationDecidedV1.EventType, eventRow.Type);
            Assert.Contains("reject@example.test", eventRow.Payload, StringComparison.Ordinal);
            Assert.Contains("0899991111", eventRow.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("internal reviewer note", eventRow.Payload, StringComparison.Ordinal);

            var corrected = await service.SaveDraftAsync(
                fixture.Session, fixture.Draft("corrected@example.test", "0811112222"), rejected.Registration.Version, default);
            var second = await service.SubmitAsync(fixture.Session, "submit-2", corrected.Version, default);
            Assert.Equal(first.Registration.RegistrationId, second.Registration.RegistrationId);
            Assert.Equal(2, second.Attempt.AttemptNo);
            Assert.Equal(2, await db.AgentRegistrationAttempts.CountAsync());
            Assert.Equal(1, await db.AgentRegistrationAttempts.CountAsync(x => x.AttemptNo == 1 && x.Status == AgentRegistrationAttemptStatus.Rejected));
            Assert.Equal(1, await db.AgentRegistrationAttempts.CountAsync(x => x.AttemptNo == 2 && x.Status == AgentRegistrationAttemptStatus.Pending));
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-4.5")]
    [Trait("Requirement", "REQ-4.6")]
    [Trait("Requirement", "REQ-4.7")]
    [Trait("Requirement", "REQ-4.9")]
    public async Task Sql_approval_revalidates_sale_versions_and_bound_sale_and_commits_one_race_winner_atomically()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var db = NewContext();
            var service = new AgentRegistrationService(NewStore(db));

            var changedRegistration = await SubmitNewAsync(service, fixture, "changed@example.test", "submit-changed");
            await using (var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(Database)))
            {
                await IntegrationDb.ExecAsync(connection,
                    "UPDATE merch.Sales SET Version = Version + 1 WHERE Id=@sale;",
                    ("@sale", fixture.SaleId));
            }
            await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
                changedRegistration.Registration.RegistrationId, changedRegistration.Attempt.AttemptId,
                Guid.NewGuid(), "official-record-1", "approve-changed", changedRegistration.Registration.Version, default));
            Assert.Equal(0, await db.Accounts.CountAsync(x => x.AccountType == AccountType.Agent));
            Assert.Equal(0, await db.GovernanceOutboxMessages.CountAsync());
            await service.RejectAsync(changedRegistration.Registration.RegistrationId, changedRegistration.Attempt.AttemptId,
                Guid.NewGuid(), "context changed", "test cleanup", "cleanup-changed",
                changedRegistration.Registration.Version, default);
            await ClearOutboxAsync();

            var boundRegistration = await SubmitNewAsync(service, fixture, "bound@example.test", "submit-bound");
            await BindSaleToOtherAgentAsync(fixture.SaleId, fixture.MerchantId);
            await Assert.ThrowsAsync<ConflictException>(() => service.ApproveAsync(
                boundRegistration.Registration.RegistrationId, boundRegistration.Attempt.AttemptId,
                Guid.NewGuid(), "official-record-2", "approve-bound", boundRegistration.Registration.Version, default));
            Assert.Equal(0, await db.GovernanceOutboxMessages.CountAsync());

            await UnbindAllAgentsAsync();
            await service.RejectAsync(boundRegistration.Registration.RegistrationId, boundRegistration.Attempt.AttemptId,
                Guid.NewGuid(), "sale bound", "test cleanup", "cleanup-bound",
                boundRegistration.Registration.Version, default);
            await ClearOutboxAsync();
            var raceRegistration = await SubmitNewAsync(service, fixture, "race@example.test", "submit-race");
            var approveTask = DecideWithNewStoreAsync(raceRegistration, fixture, approve: true);
            var rejectTask = DecideWithNewStoreAsync(raceRegistration, fixture, approve: false);
            var race = await Task.WhenAll(RecordAsync(approveTask), RecordAsync(rejectTask));
            Assert.Equal(1, race.Count(x => x.Succeeded));
            Assert.Equal(1, race.Count(x => !x.Succeeded));

            db.ChangeTracker.Clear();
            var final = await db.AgentRegistrations.SingleAsync(x => x.Id == raceRegistration.Registration.RegistrationId);
            var finalAttempt = await db.AgentRegistrationAttempts.SingleAsync(x => x.Id == raceRegistration.Attempt.AttemptId);
            Assert.NotEqual(AgentRegistrationStatus.Pending, final.Status);
            Assert.NotEqual(AgentRegistrationAttemptStatus.Pending, finalAttempt.Status);
            Assert.Equal(1, await db.GovernanceOutboxMessages.CountAsync());
            if (final.Status == AgentRegistrationStatus.Approved)
            {
                Assert.Equal(1, await db.Accounts.CountAsync(x => x.AccountType == AccountType.Agent));
                Assert.Equal(1, await db.LoginAccounts.CountAsync(x => x.ExternalUserId == "race-user"));
                Assert.Equal(1, await db.Agents.CountAsync());
                Assert.Equal(1, await db.AccountMerchantAccess.CountAsync());
                Assert.Equal(1, await db.AccessRoles.CountAsync());
            }
            else
            {
                Assert.Equal(0, await db.Accounts.CountAsync(x => x.AccountType == AccountType.Agent));
            }
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-4.5")]
    [Trait("Requirement", "REQ-4.7")]
    [Trait("Requirement", "REQ-4.12")]
    public async Task Sql_approve_success_commits_account_login_agent_access_role_decision_and_contact_outbox_atomically()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await using var db = NewContext();
            var service = new AgentRegistrationService(NewStore(db));
            var submitted = await SubmitNewAsync(service, fixture, "approved@example.test", "submit-approved");
            var approved = await service.ApproveAsync(
                submitted.Registration.RegistrationId, submitted.Attempt.AttemptId, Guid.NewGuid(),
                "official-business-record-1", "approve-approved", submitted.Registration.Version, default);

            db.ChangeTracker.Clear();
            var registration = await db.AgentRegistrations.SingleAsync(x => x.Id == approved.Registration.RegistrationId);
            var attempt = await db.AgentRegistrationAttempts.SingleAsync(x => x.Id == approved.Attempt.AttemptId);
            Assert.Equal(AgentRegistrationStatus.Approved, registration.Status);
            Assert.Equal(AgentRegistrationAttemptStatus.Approved, attempt.Status);
            Assert.Equal(1, await db.Accounts.CountAsync(x => x.AccountType == AccountType.Agent));
            Assert.Equal(1, await db.LoginAccounts.CountAsync(x => x.ExternalUserId == "race-user"));
            Assert.Equal(1, await db.Agents.CountAsync(x => x.SaleId == fixture.SaleId));
            Assert.Equal(1, await db.AccountMerchantAccess.CountAsync(x => x.MerchantId == fixture.MerchantId));
            Assert.Equal(1, await db.AccessRoles.CountAsync());
            var outbox = await db.GovernanceOutboxMessages.SingleAsync();
            Assert.Equal(AgentRegistrationDecidedV1.EventType, outbox.Type);
            Assert.Contains("approved@example.test", outbox.Payload, StringComparison.Ordinal);
            Assert.Contains("0800000000", outbox.Payload, StringComparison.Ordinal);
            Assert.DoesNotContain("official-business-record-1", outbox.Payload, StringComparison.Ordinal);
        }
        finally
        {
            await DropDatabaseAsync();
        }
    }

    private static async Task<RegistrationSubmitResult> SubmitNewAsync(
        AgentRegistrationService service, Fixture fixture, string email, string key)
    {
        var registration = await service.SaveDraftAsync(fixture.Session, fixture.Draft(email, "0800000000"), null, default);
        return await service.SubmitAsync(fixture.Session, key, registration.Version, default);
    }

    private static async Task<DecisionResult> DecideWithNewStoreAsync(
        RegistrationSubmitResult result, Fixture fixture, bool approve)
    {
        await using var db = NewContext();
        var service = new AgentRegistrationService(NewStore(db));
        try
        {
            var decision = approve
                ? await service.ApproveAsync(result.Registration.RegistrationId, result.Attempt.AttemptId,
                    Guid.NewGuid(), "race-contact", "race-approve", result.Registration.Version, default)
                : await service.RejectAsync(result.Registration.RegistrationId, result.Attempt.AttemptId,
                    Guid.NewGuid(), "race reject", "internal", "race-reject", result.Registration.Version, default);
            return new DecisionResult(true, decision);
        }
        catch (Exception exception) when (exception is ConflictException or ConcurrencyConflictException)
        {
            return new DecisionResult(false, null);
        }
    }

    private static async Task<DecisionResult> RecordAsync(Task<DecisionResult> task) => await task;

    private static async Task BindSaleToOtherAgentAsync(Guid saleId, Guid merchantId)
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(Database));
        var accountId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await IntegrationDb.ExecAsync(connection, """
            INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
            VALUES (@account, 2, N'Bound agent', 1, 0, @now, @now);
            INSERT acct.Agents (AccountId, MerchantId, SaleId, Metadata, Id)
            VALUES (@account, @merchant, @sale, N'{}', @account);
            """, ("@account", accountId), ("@merchant", merchantId), ("@sale", saleId), ("@now", now));
    }

    private static async Task UnbindAllAgentsAsync()
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(Database));
        await IntegrationDb.ExecAsync(connection,
            "DELETE acct.Agents; DELETE acct.Accounts WHERE AccountType=2;");
    }

    private static async Task ClearOutboxAsync()
    {
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(Database));
        await IntegrationDb.ExecAsync(connection, "DELETE admin.GovernanceOutboxMessages;");
    }

    private static async Task<Fixture> CreateFixtureAsync()
    {
        await TryDropDatabaseAsync();
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(Database);
        await using (var context = NewMigrationContext())
            await context.GetService<IMigrator>().MigrateAsync();

        var merchantId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var saleId = Guid.NewGuid();
        await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(Database));
        await IntegrationDb.InsertMerchantAsync(connection, merchantId, $"reg-{Guid.NewGuid():N}"[..24]);
        await IntegrationDb.ExecAsync(connection, """
            INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
            VALUES (@branch, @merchant, N'branch-1', N'Branch 1', 1, @now, @now, 1);
            INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
            VALUES (@sale, @merchant, @branch, N'sale-1', N'Sale 1', 1, @now, @now, 1);
            """, ("@branch", branchId), ("@merchant", merchantId), ("@sale", saleId), ("@now", DateTime.UtcNow));

        var identity = ExternalIdentity.Create("microsoft", "task4-tenant", "race-user");
        var rawSession = $"session-{Guid.NewGuid():N}";
        var session = RegistrationSession.Issue(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawSession)),
            identity, merchantId, DateTime.UtcNow, TimeSpan.FromMinutes(30));
        await using (var context = NewContext())
        {
            context.RegistrationSessions.Add(session);
            await context.SaveChangesAsync();
        }
        return new Fixture(session, merchantId, branchId, saleId);
    }

    private static async Task TryDropDatabaseAsync()
    {
        try
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(Database);
        }
        catch
        {
            // The first run has no database; later runs clean the named task database here.
        }
    }

    private static async Task DropDatabaseAsync() => await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(Database);

    private static ControlPlaneDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(Database), sql => sql.UseCompatibilityLevel(170)).Options,
        AllowAll.Instance, NoOpSecurityTelemetry.Instance);

    private static PolDbContext NewMigrationContext() => new(
        new DbContextOptionsBuilder<PolDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(Database), sql => sql.UseCompatibilityLevel(170)).Options,
        new ModuleAssemblies([
            typeof(Products.Infrastructure.ProductsModuleRegistration),
            typeof(Carts.Infrastructure.CartModuleRegistration),
            typeof(Orders.Infrastructure.OrdersModuleRegistration),
            typeof(Payments.Infrastructure.PaymentsModuleRegistration),
            typeof(Merchants.Infrastructure.MerchantsModuleRegistration),
            typeof(Admins.Infrastructure.AdminModuleRegistration),
            typeof(Iam.Infrastructure.IamModuleRegistration),
            typeof(Governance.Infrastructure.GovernanceModuleRegistration),
            typeof(Notifications.Infrastructure.NotificationsModuleRegistration),
            typeof(Accounts.Infrastructure.AccountsModuleRegistration),
            typeof(Access.Infrastructure.AccessModuleRegistration),
        ]));

    private static AgentRegistrationStore NewStore(ControlPlaneDbContext db) => new(
        db, new SystemClock(), new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        new GovernanceSqlLockManager(db));

    private sealed record Fixture(RegistrationSession Session, Guid MerchantId, Guid BranchId, Guid SaleId)
    {
        public RegistrationDraftRequest Draft(string email, string phone) =>
            new("sale-1", email, phone, JsonDocument.Parse("{\"displayName\":\"Task4 Applicant\"}").RootElement.Clone());
    }

    private sealed record DecisionResult(bool Succeeded, RegistrationDecisionResult? Decision);

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
