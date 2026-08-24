using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Admins.Tests;

public sealed class ResolveMicrosoftAdminTests
{
    private static readonly DateTime Now = new(2026, 8, 23, 0, 0, 0, DateTimeKind.Utc);
    private const string Email = "employee@viriyah.co.th";

    [Fact]
    public async Task Unknown_email_creates_active_scoped_roleless_account_and_internal_audit()
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, roles, audit).Handle(Command(" Employee@VIRIYAH.CO.TH "), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(User.MicrosoftProvider, account.Provider);
        Assert.Equal(Email, account.Subject);
        Assert.Equal(Email, account.Email);
        Assert.Equal(Email, account.WorkforceEmailKey);
        Assert.Equal(Tier.Scoped, account.Tier);
        Assert.Equal(UserStatus.Active, account.Status);
        Assert.Empty(admins.Assignments);
        Assert.Empty(roles.Assignments);
        var entry = Assert.Single(audit.Appended);
        Assert.Equal(AuditAction.JitProvision, entry.Action);
        Assert.Equal(account.Id, entry.ActorId);
        Assert.Equal(account.Id, entry.TargetAdminId);
    }

    [Fact]
    public async Task Existing_identity_preserves_tier_roles_merchants_and_has_no_mutation_audit()
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var account = User.JitProvisionMicrosoft(Email, Now);
        admins.Add(account);
        var merchant = Guid.NewGuid();
        admins.AddAssignment(MerchantAccess.Create(account.Id, merchant, Guid.NewGuid(), Now));
        var role = Role.Create("operator", "Operator", null, "blue", RoleStatus.Active,
            Scope.Platform, null, [Keys.UserView], Keys.KeySide);
        roles.Roles.Add(role);
        roles.AddAssignment(RoleAssignment.Create(account.Id, role.Id, Guid.NewGuid(), Now));
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, roles, audit).Handle(Command(), default);

        Assert.Equal(account.Id, result.Resolution!.AdminId);
        Assert.True(result.Resolution.Accessible.Allows(merchant));
        Assert.Contains(Keys.UserView, result.Resolution.Permissions);
        Assert.Empty(audit.Appended);
        Assert.Single(admins.Accounts);
    }

    [Fact]
    public async Task Active_unbound_email_owner_binds_in_place_and_writes_one_binding_audit()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.CreateScoped(" Employee@VIRIYAH.CO.TH ", Now);
        admins.Add(account);
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, new FakeAdminRoleRepository(), audit).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(account.Id, result.Resolution!.AdminId);
        Assert.Equal(User.MicrosoftProvider, account.Provider);
        Assert.Equal(Email, account.Subject);
        Assert.Equal("Employee@VIRIYAH.CO.TH", account.Email);
        Assert.Single(admins.Accounts);
        var entry = Assert.Single(audit.Appended);
        Assert.Equal(AuditAction.MicrosoftEmailBind, entry.Action);
        Assert.Equal(account.Id, entry.ActorId);
        Assert.Equal(account.Id, entry.TargetAdminId);
    }

    [Fact]
    public async Task Suspended_email_owner_is_denied_without_binding_or_audit()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.CreateScoped(Email, Now);
        account.Suspend(Guid.NewGuid());
        admins.Add(account);
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, new FakeAdminRoleRepository(), audit).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Suspended, result.Outcome);
        Assert.Null(account.Subject);
        Assert.Empty(audit.Appended);
    }

    [Fact]
    public async Task Bound_other_identity_is_a_conflict_without_overwrite()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.CreateScoped(Email, Now);
        account.BindSubject(User.GoogleProvider, "google-subject");
        admins.Add(account);

        var result = await Handler(admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter())
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(User.GoogleProvider, account.Provider);
        Assert.Equal("google-subject", account.Subject);
    }

    [Fact]
    public async Task Identity_and_email_divergence_fails_closed()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.CreateScoped("different@viriyah.co.th", Now);
        account.BindSubject(User.MicrosoftProvider, Email);
        admins.Add(account);

        var result = await Handler(admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter())
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
    }

    [Fact]
    public async Task Two_candidates_fail_closed()
    {
        var admins = new FakePlatformUserRepository();
        admins.Add(User.JitProvisionMicrosoft(Email, Now));
        admins.Add(User.CreateScoped(Email, Now));

        var result = await Handler(admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter())
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
    }

    [Fact]
    public async Task Identity_inserted_while_waiting_for_lock_is_reused_without_duplicate_audit()
    {
        var existing = User.JitProvisionMicrosoft(Email, Now);
        var admins = new FakePlatformUserRepository
        {
            AfterIdentityMutationLockAcquired = repository => repository.Add(existing)
        };
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, new FakeAdminRoleRepository(), audit).Handle(Command(), default);

        Assert.Equal(existing.Id, result.Resolution!.AdminId);
        Assert.Single(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.Equal(1, admins.IdentityMutationLockCalls);
    }

    [Fact]
    public async Task Unique_conflict_re_resolves_only_an_exact_fresh_context_winner()
    {
        var recovery = new FakeRecoveryReader(ResolveResult.Of(
            new Resolution(Guid.NewGuid(), Email, Tier.Scoped, AccessibleMerchants.Of(new HashSet<Guid>()))));
        var handler = Handler(
            new FakePlatformUserRepository(), new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter(),
            new ThrowingUnitOfWork(), recovery);

        var result = await handler.Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(Email, recovery.CanonicalEmail);
    }

    [Fact]
    public async Task Unresolvable_unique_conflict_stays_an_identity_conflict()
    {
        var recovery = new FakeRecoveryReader(ResolveResult.IdentityConflict);
        var handler = Handler(
            new FakePlatformUserRepository(), new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter(),
            new ThrowingUnitOfWork(), recovery);

        Assert.Equal(ResolveOutcome.IdentityConflict, (await handler.Handle(Command(), default)).Outcome);
    }

    private static ResolveMicrosoftAdminHandler Handler(
        FakePlatformUserRepository admins,
        FakeAdminRoleRepository roles,
        FakePlatformUserAuditWriter audit,
        IUnitOfWork? unitOfWork = null,
        IAdminIdentityRecoveryReader? recovery = null) =>
        new(admins, roles, audit, recovery ?? new FakeRecoveryReader(ResolveResult.IdentityConflict),
            unitOfWork ?? new FakeUnitOfWork(), new FixedClock { UtcNow = Now });

    private static ResolveMicrosoftAdminCommand Command(string email = Email) => new(email, "corr-1");

    private sealed class FakeRecoveryReader(ResolveResult result) : IAdminIdentityRecoveryReader
    {
        public int Calls { get; private set; }
        public string? CanonicalEmail { get; private set; }

        public Task<ResolveResult> ResolveAfterConflictAsync(string canonicalEmail, CancellationToken ct)
        {
            Calls++;
            CanonicalEmail = canonicalEmail;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) =>
            throw new ConflictException("unique conflict");

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation, CancellationToken ct) => await operation(ct);
    }
}
