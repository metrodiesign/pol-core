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
[Trait("Category", "Integration")]
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
        try
        {
            var fixture = await CreateFixtureAsync();
            await using var db = NewContext();
            var store = NewStore(db);
            var service = new AgentRegistrationService(store);
            var draft = fixture.Draft("first@example.test", "0812345678");

            var registration = await service.SaveDraftAsync(fixture.Session, draft, null, default);
            registration = await service.SavePhotosAsync(fixture.Session, fixture.Photos, registration.Version, default);
            Assert.Equal(AgentRegistrationStatus.Draft, registration.Status);
            Assert.Equal(0, registration.CurrentAttemptNo);
            Assert.Equal(0, await db.AgentRegistrationAttempts.CountAsync());
            Assert.Equal(0, await db.Accounts.CountAsync(x => x.Id == registration.Id));

            var gate = await Assert.ThrowsAsync<InvalidRequestException>(() =>
                service.SubmitAsync(fixture.Session, "unverified", registration.Version, default));
            Assert.Equal("phone_verification_required", gate.Code);
            registration = await VerifyAsync(service, fixture.Session);
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
        try
        {
            var fixture = await CreateFixtureAsync();
            await using var db = NewContext();
            var service = new AgentRegistrationService(NewStore(db));
            var registration = await service.SaveDraftAsync(
                fixture.Session, fixture.Draft("reject@example.test", "0899991111"), null, default);
            registration = await service.SavePhotosAsync(fixture.Session, fixture.Photos, registration.Version, default);
            registration = await VerifyAsync(service, fixture.Session);
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
            corrected = await VerifyAsync(service, fixture.Session);
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
        try
        {
            var fixture = await CreateFixtureAsync();
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
        try
        {
            var fixture = await CreateFixtureAsync();
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

    [Fact]
    [Trait("Requirement", "Issue-274")]
    public async Task Anonymous_approval_creates_account_then_callback_binds_login_idempotently()
    {
        try
        {
            var fixture = await CreateFixtureAsync();
            await using var db = NewContext();
            var service = new AgentRegistrationService(NewStore(db));
            var created = await service.StartAnonymousAsync(fixture.MerchantId, fixture.Draft("anon@example.test", "0812345678"),
                DateTime.UtcNow, TimeSpan.FromMinutes(30), default);
            var session = (await service.ResolveSessionAsync(created.RawReference, default))!;
            Assert.Null(created.Registration.Identity);
            Assert.Equal(created.Registration.Id, session.RegistrationId);
            var registration = await service.SavePhotosAsync(session, fixture.Photos, created.Registration.Version, default);
            registration = await VerifyAsync(service, session);
            var submitted = await service.SubmitAsync(session, "anonymous-submit", registration.Version, default);
            var approved = await service.ApproveAsync(registration.Id, submitted.Attempt.AttemptId, Guid.NewGuid(),
                "reviewer-evidence", "anonymous-approve", submitted.Registration.Version, default);
            Assert.Equal(AgentRegistrationStatus.Approved, approved.Registration.Status);
            Assert.NotNull((await service.GetCaseAsync(session, default))!.AccountId);
            Assert.Equal(0, await db.LoginAccounts.CountAsync());
            var identity = ExternalIdentity.Create("microsoft", "anonymous-tenant", "anonymous-person");
            var bound = await service.BindIdentityByEmailAsync(identity, " ANON@EXAMPLE.TEST ", "Agent", fixture.MerchantId, default);
            Assert.Equal(registration.Id, bound!.Id);
            Assert.Equal(bound.AccountId, (await db.LoginAccounts.SingleAsync()).AccountId);
            await service.BindIdentityByEmailAsync(identity, "no-longer-the-email@example.test", "Agent", fixture.MerchantId, default);
            Assert.Equal(1, await db.LoginAccounts.CountAsync());
            var conflict = await Assert.ThrowsAsync<IdentityAccessException>(() => service.BindIdentityByEmailAsync(
                identity with { ExternalUserId = "another" }, "anon@example.test", "Other", fixture.MerchantId, default));
            Assert.Equal("registration_identity_conflict", conflict.Code);
        }
        finally { await DropDatabaseAsync(); }
    }

    [Fact]
    [Trait("Requirement", "Issue-274")]
    [Trait("Requirement", "AC-PHONE-5")]
    [Trait("Requirement", "AC-PHONE-6")]
    [Trait("Requirement", "AC-PHONE-7")]
    public async Task Otp_attempts_cooldown_send_limit_and_phone_binding_persist_in_sql()
    {
        try
        {
            var fixture = await CreateFixtureAsync();
            var clock = new VerificationClock { UtcNow = DateTime.UtcNow };
            await using var db = NewContext();
            var store = new AgentRegistrationStore(db, clock, new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance), new GovernanceSqlLockManager(db));
            var service = new AgentRegistrationService(store);
            var registration = await service.SaveDraftAsync(fixture.Session, fixture.Draft("otp@example.test", "0812345678"), null, default);
            var first = await service.IssueContactVerificationAsync(fixture.Session, default);
            var cooldown = await Assert.ThrowsAsync<ConflictException>(() => service.IssueContactVerificationAsync(fixture.Session, default));
            Assert.Equal("verification_cooldown", cooldown.Code);
            var wrong = first.Code == "000000" ? "000001" : "000000";
            for (var i = 0; i < 5; i++)
            {
                var invalid = await Assert.ThrowsAsync<InvalidRequestException>(() => service.ConfirmContactVerificationAsync(fixture.Session, first.Verification.Id, wrong, default));
                Assert.Equal("verification_code_invalid", invalid.Code);
                db.ChangeTracker.Clear();
            }
            Assert.Equal(5, (await db.ContactVerifications.AsNoTracking().SingleAsync()).Attempts);
            var exhausted = await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmContactVerificationAsync(fixture.Session, first.Verification.Id, first.Code, default));
            Assert.Equal("verification_attempts_exceeded", exhausted.Code);
            ContactVerificationIssue latest = first;
            for (var i = 1; i < 5; i++)
            {
                clock.UtcNow = clock.UtcNow.AddSeconds(60);
                latest = await service.IssueContactVerificationAsync(fixture.Session, default);
            }
            clock.UtcNow = clock.UtcNow.AddSeconds(60);
            var limited = await Assert.ThrowsAsync<ConflictException>(() => service.IssueContactVerificationAsync(fixture.Session, default));
            Assert.Equal("verification_send_limit", limited.Code);
            registration = (await service.ConfirmContactVerificationAsync(fixture.Session, latest.Verification.Id, latest.Code, default)).Registration;
            registration = await service.SaveDraftAsync(fixture.Session, fixture.Draft("otp@example.test", "0812345678"), registration.Version, default);
            Assert.True(registration.PhoneVerified);
            registration = await service.SaveDraftAsync(fixture.Session, fixture.Draft("otp@example.test", "0899999999"), registration.Version, default);
            Assert.False(registration.PhoneVerified);
            Assert.Null(registration.PhoneVerifiedAt);
            Assert.Null(registration.PhoneVerifiedNumber);
            db.ChangeTracker.Clear();
            var mismatch = await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmContactVerificationAsync(fixture.Session, latest.Verification.Id, latest.Code, default));
            Assert.Equal("phone_mismatch", mismatch.Code);
            Assert.Equal(5, await db.ContactVerifications.CountAsync());
            var changed = await service.IssueContactVerificationAsync(fixture.Session, default);
            Assert.Equal("0899999999", changed.Verification.Recipient);
            clock.UtcNow = clock.UtcNow.AddMinutes(5);
            var expired = await Assert.ThrowsAsync<ConflictException>(() => service.ConfirmContactVerificationAsync(
                fixture.Session, changed.Verification.Id, changed.Code, default));
            Assert.Equal("verification_expired", expired.Code);
            await Assert.ThrowsAsync<NotFoundException>(() => service.ConfirmContactVerificationAsync(
                fixture.Session, Guid.NewGuid(), changed.Code, default));
        }
        finally { await DropDatabaseAsync(); }
    }

    [Fact]
    [Trait("Requirement", "Issue-274")]
    public async Task Pending_binding_and_rejected_email_edit_keep_identity_first_recovery()
    {
        try
        {
            var fixture = await CreateFixtureAsync();
            await using var db = NewContext();
            var service = new AgentRegistrationService(NewStore(db));
            var created = await service.StartAnonymousAsync(fixture.MerchantId, fixture.Draft("original@example.test", "0812345678"),
                DateTime.UtcNow, TimeSpan.FromMinutes(30), default);
            var session = (await service.ResolveSessionAsync(created.RawReference, default))!;
            var registration = await service.SavePhotosAsync(session, fixture.Photos, created.Registration.Version, default);
            registration = await VerifyAsync(service, session);
            var submitted = await service.SubmitAsync(session, "pending-bind", registration.Version, default);
            var identity = ExternalIdentity.Create("microsoft", "pending-tenant", "pending-person");
            var bound = await service.BindIdentityByEmailAsync(identity, "original@example.test", "Agent", fixture.MerchantId, default);
            Assert.Equal(AgentRegistrationStatus.Pending, bound!.Status);
            Assert.Equal(identity, bound.Identity);
            Assert.Null(bound.AccountId);
            Assert.Empty(await db.LoginAccounts.ToListAsync());
            var rejected = await service.RejectAsync(bound.Id, submitted.Attempt.AttemptId, Guid.NewGuid(), "correct email",
                null, "reject-bind", bound.Version, default);
            await service.SaveDraftAsync(session, fixture.Draft("edited@example.test", "0812345678"), rejected.Registration.Version, default);
            await service.StartAnonymousAsync(fixture.MerchantId, fixture.Draft("original@example.test", "0899999999"),
                DateTime.UtcNow, TimeSpan.FromMinutes(30), default);
            var recovered = await service.BindIdentityByEmailAsync(identity, "original@example.test", "Agent", fixture.MerchantId, default);
            Assert.Equal(bound.Id, recovered!.Id);
            Assert.Equal("edited@example.test", recovered.Email);
            var foreign = await Assert.ThrowsAsync<IdentityAccessException>(() => service.BindIdentityByEmailAsync(identity,
                "original@example.test", "Agent", Guid.NewGuid(), default));
            Assert.Equal("identity_already_bound", foreign.Code);
            Assert.Null(await service.BindIdentityByEmailAsync(identity with { ExternalUserId = "missing" },
                "missing@example.test", "Missing", fixture.MerchantId, default));
        }
        finally { await DropDatabaseAsync(); }
    }

    [Fact]
    [Trait("Requirement", "AC-PHONE-3")]
    [Trait("Requirement", "AC-PHONE-4")]
    public async Task Submission_revalidates_a_legacy_noncanonical_phone_before_verification_gate()
    {
        try
        {
            var fixture = await CreateFixtureAsync();
            await using var db = NewContext();
            var service = new AgentRegistrationService(NewStore(db));
            var registration = await service.SaveDraftAsync(fixture.Session, fixture.Draft("legacy@example.test", "0812345678"), null, default);
            registration = await service.SavePhotosAsync(fixture.Session, fixture.Photos, registration.Version, default);
            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(Database));
            await IntegrationDb.ExecAsync(connection, "UPDATE acct.AgentRegistrations SET PhoneNumber=N'+66812345678' WHERE Id=@id;", ("@id", registration.Id));
            db.ChangeTracker.Clear();
            var invalid = await Assert.ThrowsAsync<InvalidRequestException>(() => service.SubmitAsync(fixture.Session, "legacy-phone", registration.Version, default));
            Assert.Equal("phone_invalid", invalid.Code);
            Assert.Empty(await db.AgentRegistrationAttempts.ToListAsync());
        }
        finally { await DropDatabaseAsync(); }
    }

    [Theory]
    [InlineData(false, 51000)]
    [InlineData(true, 51001)]
    [Trait("Requirement", "Issue-274")]
    public async Task Migration_refuses_duplicate_email_or_unlinked_approved_case_without_changing_rows(bool approved, int errorNumber)
    {
        try
        {
            await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(Database);
            await using var migration = NewMigrationContext();
            await migration.GetService<IMigrator>().MigrateAsync("20260918014926_DropAgentIdColumn");
            await using var connection = await IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(Database));
            await IntegrationDb.ExecAsync(connection, """
                INSERT acct.AgentRegistrations
                    (Id, MerchantId, Provider, TenantId, ExternalUserId, CurrentAttemptNo, Status,
                     SaleCode, Email, PhoneNumber, ProfileJson, CreatedAt, UpdatedAt, Version)
                VALUES (@id, @merchant, N'microsoft', N'migration-tenant', N'one', 0, @status,
                        N'sale', N' Duplicate@Example.Test ', N'0812345678', N'{}', SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                """, ("@id", Guid.NewGuid()), ("@merchant", Guid.Empty), ("@status", approved ? 3 : 1));
            if (!approved)
                await IntegrationDb.ExecAsync(connection, """
                    INSERT acct.AgentRegistrations
                        (Id, MerchantId, Provider, TenantId, ExternalUserId, CurrentAttemptNo, Status,
                         SaleCode, Email, PhoneNumber, ProfileJson, CreatedAt, UpdatedAt, Version)
                    VALUES (@id, @merchant, N'microsoft', N'migration-tenant', N'two', 0, 4,
                            N'sale', N'duplicate@example.test', N'0899999999', N'{}', SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                    """, ("@id", Guid.NewGuid()), ("@merchant", Guid.Empty));
            var exception = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => migration.GetService<IMigrator>().MigrateAsync());
            Assert.Equal(errorNumber, exception.Number);
            Assert.Equal(approved ? 1 : 2, Convert.ToInt32(await IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM acct.AgentRegistrations;")));
            Assert.Equal(DBNull.Value, await IntegrationDb.ScalarAsync(connection,
                "SELECT COL_LENGTH('acct.AgentRegistrations', 'EmailNormalized');"));
        }
        finally { await DropDatabaseAsync(); }
    }

    private sealed class VerificationClock : IClock
    {
        public DateTime UtcNow { get; set; }
    }

    private static async Task<AgentRegistration> VerifyAsync(AgentRegistrationService service, RegistrationSession session)
    {
        var current = await service.GetCaseAsync(session, default);
        if (current!.PhoneVerified)
            return current;
        var issue = await service.IssueContactVerificationAsync(session, default);
        return (await service.ConfirmContactVerificationAsync(session, issue.Verification.Id, issue.Code, default)).Registration;
    }

    private static async Task<RegistrationSubmitResult> SubmitNewAsync(
        AgentRegistrationService service, Fixture fixture, string email, string key)
    {
        var registration = await service.SaveDraftAsync(fixture.Session, fixture.Draft(email, "0800000000"), null, default);
        registration = await service.SavePhotosAsync(fixture.Session, fixture.Photos, registration.Version, default);
        registration = await VerifyAsync(service, fixture.Session);
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
            INSERT acct.Agents (AccountId, MerchantId, SaleId, Metadata)
            VALUES (@account, @merchant, @sale, N'{}');
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
            new("sale-1", email, phone, JsonDocument.Parse(
                "{\"schemaVersion\":1,\"firstName\":\"Task4\",\"lastName\":\"Applicant\",\"personType\":\"Individual\",\"idNumber\":\"1234567890123\"}")
                .RootElement.Clone());

        // Submit refuses a case without a photo (photo_required); the SQL tests attach a placeholder key.
        public RegistrationPhotos Photos => new("photos/task4.png", "image/png", null, null);
    }

    private sealed record DecisionResult(bool Succeeded, RegistrationDecisionResult? Decision);

    private sealed class AllowAll : IWriteAuthorizer
    {
        public static readonly AllowAll Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
