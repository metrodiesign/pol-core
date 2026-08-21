using System.Security.Cryptography;
using System.Text;
using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;

namespace Admins.Tests;

public sealed class PreProvisionMicrosoftIdentityTests
{
    private static readonly DateTime Now = new(2026, 8, 19, 4, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [Fact]
    public async Task Success_binds_once_preserves_authorization_and_writes_audit_and_non_expiring_replay()
    {
        var positionId = Guid.NewGuid();
        var officeId = Guid.NewGuid();
        var target = User.CreateScoped("employee@org.com", Now, positionId, officeId);
        var setup = NewHandler(target);
        var merchantId = Guid.NewGuid();
        setup.Repository.AddAssignment(MerchantAccess.Create(target.Id, merchantId, setup.Actor.Id, Now));

        var result = await setup.Handler.Handle(Command(setup, reason: "  HR onboarding  "), default);

        Assert.Equal(target.Id, result.AdminId);
        Assert.Equal(User.MicrosoftProvider, result.Provider);
        Assert.True(result.SubjectBound);
        Assert.Equal(2, result.Version);
        Assert.Equal(ObjectId.ToString("D"), target.Subject);
        Assert.Equal("employee@org.com", target.Email);
        Assert.Equal(Tier.Scoped, target.Tier);
        Assert.Equal(UserStatus.Active, target.Status);
        Assert.Equal(positionId, target.PositionId);
        Assert.Equal(officeId, target.OfficeId);
        Assert.Equal(0, target.AuthorizationVersion);
        Assert.Contains(setup.Repository.Assignments, x => x.AdminUserId == target.Id && x.MerchantId == merchantId);

        var audit = Assert.Single(setup.Audit.Appended);
        Assert.Equal(setup.Actor.Id, audit.ActorAdminId);
        Assert.Equal(target.Id, audit.TargetAdminId);
        Assert.Equal("HR onboarding", audit.Reason);
        Assert.Equal(2, audit.ResourceVersion);
        Assert.Equal("corr-1", audit.CorrelationId);
        Assert.Equal(Now, audit.OccurredAt);
        Assert.Equal(Fingerprint(TenantId, ObjectId), audit.IdentityFingerprint);
        Assert.DoesNotContain(TenantId.ToString("D"), audit.IdentityFingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ObjectId.ToString("D"), audit.IdentityFingerprint, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, setup.Operations.Count);
        Assert.Equal(200, setup.Operations.LastResponseStatus);
        Assert.Equal(DateTime.MaxValue, setup.Operations.LastExpiresAt);
        Assert.Equal(1, setup.Repository.IdentityMutationLockCalls);
    }

    [Fact]
    public async Task Suspended_target_can_be_reserved_without_reactivation_or_authorization_change()
    {
        var target = User.CreateScoped("employee@org.com", Now);
        target.Suspend(Guid.NewGuid());
        var authorizationVersion = target.AuthorizationVersion;
        var setup = NewHandler(target);

        var result = await setup.Handler.Handle(Command(setup), default);

        Assert.True(result.SubjectBound);
        Assert.Equal(UserStatus.Suspended, target.Status);
        Assert.Equal(Tier.Scoped, target.Tier);
        Assert.Equal(authorizationVersion, target.AuthorizationVersion);
        Assert.Equal(3, target.Version);
    }

    [Fact]
    public async Task Exact_replay_wins_over_provider_and_current_target_version_gates()
    {
        var setup = NewHandler(User.CreateScoped("employee@org.com", Now));
        var first = await setup.Handler.Handle(Command(setup), default);
        var replay = Command(setup) with
        {
            ConfiguredWorkforceTenantId = null,
            ExpectedTargetVersion = long.MaxValue
        };

        var second = await setup.Handler.Handle(replay, default);

        Assert.Equal(first, second);
        Assert.Single(setup.Audit.Appended);
        Assert.Equal(1, setup.Operations.Count);
        Assert.Equal(1, setup.Repository.IdentityMutationLockCalls);
    }

    [Fact]
    public async Task Natural_no_op_keeps_version_and_writes_no_state_change_audit()
    {
        var target = User.CreateScoped("employee@org.com", Now);
        target.BindSubject(User.MicrosoftProvider, ObjectId.ToString("D"));
        var setup = NewHandler(target);
        var version = target.Version;

        var result = await setup.Handler.Handle(Command(setup, key: "new-key"), default);

        Assert.Equal(version, result.Version);
        Assert.Equal(version, target.Version);
        Assert.Empty(setup.Audit.Appended);
        Assert.Equal(1, setup.Operations.Count);
        Assert.Equal(DateTime.MaxValue, setup.Operations.LastExpiresAt);
    }

    [Fact]
    public async Task Natural_no_op_still_requires_current_target_version()
    {
        var target = User.CreateScoped("employee@org.com", Now);
        target.BindSubject(User.MicrosoftProvider, ObjectId.ToString("D"));
        var setup = NewHandler(target);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => setup.Handler.Handle(
            Command(setup, key: "stale-no-op") with { ExpectedTargetVersion = target.Version - 1 }, default).AsTask());

        Assert.Equal("state_conflict", error.Code);
        Assert.Equal(2, target.Version);
        Assert.Empty(setup.Audit.Appended);
        Assert.Equal(0, setup.Operations.Count);
    }

    [Fact]
    public async Task Reusing_key_for_different_intent_is_rejected_before_current_state_gates()
    {
        var setup = NewHandler(User.CreateScoped("employee@org.com", Now));
        await setup.Handler.Handle(Command(setup), default);

        var error = await Assert.ThrowsAsync<ConflictException>(() => setup.Handler.Handle(
            Command(setup, reason: "different reason") with { ConfiguredWorkforceTenantId = null }, default).AsTask());

        Assert.Equal("idempotency_key_reused", error.Code);
        Assert.Single(setup.Audit.Appended);
    }

    [Fact]
    public async Task In_progress_record_fails_closed()
    {
        var setup = NewHandler(User.CreateScoped("employee@org.com", Now));
        var command = Command(setup);
        setup.Operations.Seed(setup.Actor.Id, "PreProvisionMicrosoftIdentity", command.IdempotencyKey,
            new AdminOperationReplay(IntentHash(command), null, InProgress: true));

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            setup.Handler.Handle(command, default).AsTask());

        Assert.Equal("operation_in_progress", error.Code);
        Assert.Null(setup.Target.Subject);
        Assert.Empty(setup.Audit.Appended);
    }

    [Fact]
    public async Task Provider_disabled_and_tenant_mismatch_are_rejected_without_writes()
    {
        var setup = NewHandler(User.CreateScoped("employee@org.com", Now));

        var disabled = await Assert.ThrowsAsync<ConflictException>(() => setup.Handler.Handle(
            Command(setup, key: "disabled") with { ConfiguredWorkforceTenantId = null }, default).AsTask());
        var mismatch = await Assert.ThrowsAsync<InvalidRequestException>(() => setup.Handler.Handle(
            Command(setup, key: "mismatch") with { ConfiguredWorkforceTenantId = Guid.NewGuid() }, default).AsTask());

        Assert.Equal("microsoft_provider_disabled", disabled.Code);
        Assert.Equal("entra_tenant_mismatch", mismatch.Code);
        Assert.Null(setup.Target.Subject);
        Assert.Empty(setup.Audit.Appended);
        Assert.Equal(0, setup.Operations.Count);
    }

    [Fact]
    public async Task Active_Super_lease_is_required_before_binding()
    {
        var setup = NewHandler(User.CreateScoped("employee@org.com", Now));
        setup.Actor.Suspend(Guid.NewGuid());

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => setup.Handler.Handle(
            Command(setup) with { ExpectedAuthorizationVersion = setup.Actor.AuthorizationVersion }, default).AsTask());

        Assert.Equal("super_required", error.Code);
        Assert.Null(setup.Target.Subject);
        Assert.Empty(setup.Audit.Appended);
        Assert.Equal(0, setup.Operations.Count);
    }

    [Fact]
    public async Task Target_and_identity_conflicts_have_stable_codes()
    {
        var boundTarget = User.CreateScoped("bound@org.com", Now);
        boundTarget.BindSubject(User.GoogleProvider, "google-subject");
        var bound = NewHandler(boundTarget);
        var boundError = await Assert.ThrowsAsync<ConflictException>(() =>
            bound.Handler.Handle(Command(bound), default).AsTask());

        var identityTarget = User.CreateScoped("employee@org.com", Now);
        var identityOwner = User.CreateScoped("owner@org.com", Now);
        identityOwner.BindSubject(User.MicrosoftProvider, ObjectId.ToString("D"));
        var duplicate = NewHandler(identityTarget);
        duplicate.Repository.Add(identityOwner);
        var duplicateError = await Assert.ThrowsAsync<ConflictException>(() =>
            duplicate.Handler.Handle(Command(duplicate), default).AsTask());

        Assert.Equal("admin_identity_already_bound", boundError.Code);
        Assert.Equal("microsoft_identity_already_bound", duplicateError.Code);
        Assert.Empty(bound.Audit.Appended);
        Assert.Empty(duplicate.Audit.Appended);
    }

    [Fact]
    public async Task Unique_race_is_re_read_after_rollback_and_mapped_to_identity_conflict()
    {
        var unitOfWork = new FailingUnitOfWork(
            new ConflictException("unique constraint", new InvalidOperationException("simulated")));
        var setup = NewHandler(User.CreateScoped("employee@org.com", Now), unitOfWork);
        var winner = User.CreateScoped("winner@org.com", Now);
        winner.BindSubject(User.MicrosoftProvider, ObjectId.ToString("D"));
        unitOfWork.BeforeThrow = () => setup.Repository.Add(winner);

        var error = await Assert.ThrowsAsync<ConflictException>(() =>
            setup.Handler.Handle(Command(setup), default).AsTask());

        Assert.Equal("microsoft_identity_already_bound", error.Code);
        Assert.Null(error.InnerException);
        Assert.Null(setup.Target.Subject);
        Assert.Empty(setup.Audit.Appended);
        Assert.Equal(0, setup.Operations.Count);
    }

    [Fact]
    public async Task Same_target_unique_race_maps_to_state_conflict()
    {
        var unitOfWork = new FailingUnitOfWork(
            new ConflictException("unique constraint", new InvalidOperationException("simulated")));
        var target = User.CreateScoped("employee@org.com", Now);
        var setup = NewHandler(target, unitOfWork);
        unitOfWork.BeforeThrow = () => target.BindSubject(User.MicrosoftProvider, ObjectId.ToString("D"));

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            setup.Handler.Handle(Command(setup), default).AsTask());

        Assert.Equal("state_conflict", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Authorization_race_is_re_read_after_rollback_and_mapped_to_super_required()
    {
        var unitOfWork = new FailingUnitOfWork(new ConcurrencyConflictException("simulated race"));
        var setup = NewHandler(User.CreateScoped("employee@org.com", Now), unitOfWork);
        unitOfWork.BeforeThrow = () => setup.Actor.Suspend(Guid.NewGuid());

        var error = await Assert.ThrowsAsync<AccessDeniedException>(() =>
            setup.Handler.Handle(Command(setup), default).AsTask());

        Assert.Equal("super_required", error.Code);
        Assert.Null(setup.Target.Subject);
        Assert.Empty(setup.Audit.Appended);
        Assert.Equal(0, setup.Operations.Count);
    }

    [Fact]
    public async Task Missing_Super_target_and_stale_version_have_stable_failures()
    {
        var missing = NewHandler(User.CreateScoped("unused@org.com", Now));
        missing.Repository.Accounts.Remove(missing.Target);
        var notFound = await Assert.ThrowsAsync<NotFoundException>(() =>
            missing.Handler.Handle(Command(missing), default).AsTask());

        var superTarget = NewHandler(User.SelfProvision(User.GoogleProvider, "other-super", "super@org.com", Now));
        var notScoped = await Assert.ThrowsAsync<ConflictException>(() =>
            superTarget.Handler.Handle(Command(superTarget), default).AsTask());

        var stale = NewHandler(User.CreateScoped("employee@org.com", Now));
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => stale.Handler.Handle(
            Command(stale) with { ExpectedTargetVersion = stale.Target.Version + 1 }, default).AsTask());

        Assert.Equal("admin_not_found", notFound.Code);
        Assert.Equal("target_not_scoped", notScoped.Code);
        Assert.Equal("state_conflict", conflict.Code);
    }

    [Fact]
    public async Task Reason_rejects_email_and_all_request_identity_formats()
    {
        var invalid = new[]
        {
            "employee@org.com",
            TenantId.ToString("D"), TenantId.ToString("N"), TenantId.ToString("B"),
            ObjectId.ToString("P"), ObjectId.ToString("X")
        };

        foreach (var reason in invalid)
        {
            var setup = NewHandler(User.CreateScoped($"{Guid.NewGuid():N}@org.com", Now));
            var error = await Assert.ThrowsAsync<InvalidRequestException>(() =>
                setup.Handler.Handle(Command(setup, reason: reason), default).AsTask());
            Assert.Equal("invalid_reason", error.Code);
        }
    }

    private static Setup NewHandler(User target, IUnitOfWork? unitOfWork = null)
    {
        var repository = new FakePlatformUserRepository();
        var actor = User.SelfProvision(User.GoogleProvider, $"actor-{Guid.NewGuid():N}", "super@org.com", Now);
        repository.Add(actor);
        repository.Add(target);
        var audit = new FakeAdminIdentityAuditWriter();
        var operations = new FakeAdminOperationStore();
        return new Setup(
            new PreProvisionMicrosoftIdentityHandler(
                repository, audit, operations, unitOfWork ?? new FakeUnitOfWork(), new FixedClock { UtcNow = Now }),
            repository, audit, operations, actor, target);
    }

    private static PreProvisionMicrosoftIdentityCommand Command(
        Setup setup, string reason = "HR onboarding", string key = "key-1") =>
        new(
            setup.Target.Id,
            TenantId,
            ObjectId,
            reason,
            setup.Actor.Id,
            setup.Actor.AuthorizationVersion,
            setup.Target.Version,
            "corr-1",
            key,
            TenantId);

    private static string IntentHash(PreProvisionMicrosoftIdentityCommand command) => LowerHex(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{command.TargetAdminId:D}\n{command.WorkforceTenantId:D}\n{command.EntraObjectId:D}\n{command.Reason.Trim()}")));

    private static string Fingerprint(Guid tenantId, Guid objectId) =>
        $"sha256:{LowerHex(SHA256.HashData(Encoding.UTF8.GetBytes($"{tenantId:D}\n{objectId:D}")))}";

    private static string LowerHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed record Setup(
        PreProvisionMicrosoftIdentityHandler Handler,
        FakePlatformUserRepository Repository,
        FakeAdminIdentityAuditWriter Audit,
        FakeAdminOperationStore Operations,
        User Actor,
        User Target);

    private sealed class FailingUnitOfWork(Exception error) : IUnitOfWork
    {
        public Action? BeforeThrow { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Save should not be called by this race fake.");

        public Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            BeforeThrow?.Invoke();
            return Task.FromException<T>(error);
        }
    }
}
