using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using SharedKernel;

namespace Admins.Tests;

public sealed class JitProvisionMicrosoftAdminTests
{
    private static readonly DateTime Now = new(2026, 8, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string Subject = Guid.Parse("B6B3AC9E-8FA6-4D5B-9D4E-8EEA6CCB1D26").ToString("D");

    [Fact]
    public async Task Eligible_identity_creates_active_scoped_roleless_account_and_internal_audit()
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var audit = new FakePlatformUserAuditWriter();
        var result = await Handler(admins, roles, audit).Handle(Command(uppercaseSubject: true), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(User.MicrosoftProvider, account.Provider);
        Assert.Equal(Subject, account.Subject);
        Assert.Equal("employee@viriyah.co.th", account.Email);
        Assert.Equal(Tier.Scoped, account.Tier);
        Assert.Equal(UserStatus.Active, account.Status);
        Assert.Empty(admins.Assignments);
        Assert.Empty(roles.Assignments);
        var entry = Assert.Single(audit.Appended);
        Assert.Equal(AuditAction.JitProvision, entry.Action);
        Assert.Equal(account.Id, entry.ActorId);
        Assert.Equal(account.Id, entry.TargetAdminId);
        Assert.DoesNotContain(Subject, entry.CorrelationId);
        Assert.DoesNotContain(account.Email, entry.CorrelationId);
    }

    [Fact]
    public async Task Existing_identity_preserves_tier_roles_merchants_and_single_audit()
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var account = User.JitProvisionMicrosoft(Subject, "employee@viriyah.co.th", Now);
        admins.Add(account);
        var merchant = Guid.NewGuid();
        admins.AddAssignment(MerchantAccess.Create(account.Id, merchant, Guid.NewGuid(), Now));
        var role = Role.Create("operator", "Operator", null, "blue", RoleStatus.Active,
            Scope.Platform, null, [Keys.UserView], Keys.KeySide);
        roles.Roles.Add(role);
        roles.AddAssignment(RoleAssignment.Create(account.Id, role.Id, Guid.NewGuid(), Now));
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, roles, audit).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(account.Id, result.Resolution!.AdminId);
        Assert.Equal(Tier.Scoped, result.Resolution.Tier);
        Assert.True(result.Resolution.Accessible.Allows(merchant));
        Assert.Contains(Keys.UserView, result.Resolution.Permissions);
        Assert.Empty(audit.Appended);
        Assert.Single(admins.Accounts);
    }

    [Fact]
    public async Task Suspended_identity_is_denied_without_reprovisioning()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(Subject, "employee@viriyah.co.th", Now);
        account.Suspend(Guid.NewGuid());
        admins.Add(account);
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, new FakeAdminRoleRepository(), audit).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Suspended, result.Outcome);
        Assert.Single(admins.Accounts);
        Assert.Empty(audit.Appended);
    }

    [Fact]
    public async Task Email_collision_returns_typed_conflict_without_write_or_audit()
    {
        var admins = new FakePlatformUserRepository();
        admins.Add(User.CreateScoped("employee@viriyah.co.th", Now));
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, new FakeAdminRoleRepository(), audit).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Single(admins.Accounts);
        Assert.Empty(audit.Appended);
    }

    [Fact]
    public async Task Identity_inserted_while_waiting_for_lock_is_reused_without_duplicate_audit()
    {
        var existing = User.JitProvisionMicrosoft(Subject, "employee@viriyah.co.th", Now);
        var admins = new FakePlatformUserRepository
        {
            AfterIdentityMutationLockAcquired = repository => repository.Add(existing)
        };
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, new FakeAdminRoleRepository(), audit).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(existing.Id, result.Resolution!.AdminId);
        Assert.Single(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.Equal(1, admins.IdentityMutationLockCalls);
    }

    [Fact]
    public async Task Unique_conflict_re_resolves_through_recovery_port_and_does_not_report_not_found()
    {
        var admins = new FakePlatformUserRepository();
        var recovery = new FakeRecoveryReader(ResolveResult.Of(
            new Resolution(Guid.NewGuid(), "employee@viriyah.co.th", Tier.Scoped,
                AccessibleMerchants.Of(new HashSet<Guid>()))));
        var handler = Handler(
            admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter(),
            new ThrowingUnitOfWork(), recovery);

        var result = await handler.Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(1, recovery.Calls);
    }

    [Fact]
    public async Task Unresolvable_unique_conflict_fails_closed_as_identity_conflict()
    {
        var recovery = new FakeRecoveryReader(ResolveResult.IdentityConflict);
        var handler = Handler(
            new FakePlatformUserRepository(), new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter(),
            new ThrowingUnitOfWork(), recovery);

        var result = await handler.Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
    }

    private static JitProvisionMicrosoftAdminHandler Handler(
        FakePlatformUserRepository admins,
        FakeAdminRoleRepository roles,
        FakePlatformUserAuditWriter audit,
        IUnitOfWork? unitOfWork = null,
        IAdminIdentityRecoveryReader? recovery = null) =>
        new(admins, roles, audit, recovery ?? new FakeRecoveryReader(ResolveResult.IdentityConflict),
            unitOfWork ?? new FakeUnitOfWork(), new FixedClock { UtcNow = Now });

    private static JitProvisionMicrosoftAdminCommand Command(bool uppercaseSubject = false) =>
        new(new ProviderIdentity(User.MicrosoftProvider, uppercaseSubject ? Subject.ToUpperInvariant() : Subject),
            "employee@viriyah.co.th", "corr-1");

    private sealed class FakeRecoveryReader(ResolveResult result) : IAdminIdentityRecoveryReader
    {
        public int Calls { get; private set; }

        public Task<ResolveResult> ResolveAfterConflictAsync(ProviderIdentity identity, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) =>
            throw new ConflictException("unique conflict");

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            await operation(ct);
    }
}
