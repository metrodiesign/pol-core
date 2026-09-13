using Admins.Application.Users;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Admins.Tests;

public sealed class ResolveMicrosoftAdminTests
{
    private static readonly DateTime Now = new(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string Email = "Employee@VIRIYAH.CO.TH";

    [Fact]
    public async Task Unknown_tuple_creates_active_scoped_roleless_account_and_internal_audit()
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var audit = new FakePlatformUserAuditWriter();
        var unitOfWork = new FakeUnitOfWork();

        var result = await Handler(admins, roles, audit, unitOfWork).Handle(Command(email: $" {Email} "), default);

        var account = Assert.Single(admins.Accounts);
        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(User.MicrosoftProvider, account.Provider);
        Assert.Equal(TenantId, account.TenantId);
        Assert.Equal(ObjectId.ToString("D"), account.Subject);
        Assert.Equal(Email, account.Email);
        Assert.Equal(Tier.Scoped, account.Tier);
        Assert.Equal(UserStatus.Active, account.Status);
        Assert.Empty(admins.Assignments);
        Assert.Empty(roles.Assignments);
        Assert.Equal(1, admins.MicrosoftIdentityLookupCalls);
        Assert.Equal(0, admins.GenericIdentityLookupCalls);
        Assert.Equal(0, admins.EmailLookupCalls);
        Assert.Equal(0, admins.EmployeeIdLookupCalls);
        Assert.Equal(1, unitOfWork.SaveChangesCalls);
        var entry = Assert.Single(audit.Appended);
        Assert.Equal(AuditAction.JitProvision, entry.Action);
        Assert.Equal(account.Id, entry.ActorId);
        Assert.Equal(account.Id, entry.TargetAdminId);
    }

    [Fact]
    public async Task Email_less_tuple_jits_without_placeholder()
    {
        var admins = new FakePlatformUserRepository();

        var result = await Handler(
            admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter())
            .Handle(Command(email: null), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Null(Assert.Single(admins.Accounts).Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("renamed@example.com")]
    public async Task Existing_exact_identity_preserves_contact_authorization_and_merchants_without_mutation_audit(
        string? callbackEmail)
    {
        var admins = new FakePlatformUserRepository();
        var roles = new FakeAdminRoleRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, "stored@example.com", Now);
        account.ChangeTier(Tier.Super, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
        var version = account.Version;
        var authorizationVersion = account.AuthorizationVersion;
        admins.Add(account);
        var merchant = Guid.NewGuid();
        admins.AddAssignment(MerchantAccess.Create(account.Id, merchant, Guid.NewGuid(), Now));
        var role = Role.Create("operator", "Operator", null, "blue", RoleStatus.Active,
            Scope.Platform, null, [Keys.UserView], Keys.KeySide);
        roles.Roles.Add(role);
        roles.AddAssignment(RoleAssignment.Create(account.Id, role.Id, Guid.NewGuid(), Now));
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, roles, audit).Handle(Command(email: callbackEmail), default);

        Assert.Equal(account.Id, result.Resolution!.AdminId);
        Assert.Equal("stored@example.com", account.Email);
        Assert.Equal(Tier.Super, result.Resolution.Tier);
        Assert.Equal(authorizationVersion, result.Resolution.AuthorizationVersion);
        Assert.Equal(version, account.Version);
        Assert.True(result.Resolution.Accessible.Allows(merchant));
        Assert.Single(admins.Assignments);
        Assert.Contains(Keys.UserView, result.Resolution.Permissions);
        Assert.Empty(audit.Appended);
        Assert.Single(admins.Accounts);
        Assert.Equal(1, admins.MicrosoftIdentityLookupCalls);
        Assert.Equal(0, admins.GenericIdentityLookupCalls);
        Assert.Equal(0, admins.EmailLookupCalls);
    }

    [Fact]
    public async Task Same_contact_email_with_different_object_ids_creates_independent_accounts()
    {
        var admins = new FakePlatformUserRepository();
        admins.Add(User.JitProvisionMicrosoft(TenantId, Guid.NewGuid(), Email, Now));

        var result = await Handler(
            admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter())
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(2, admins.Accounts.Count);
        Assert.All(admins.Accounts, account => Assert.Equal(Email, account.Email));
        Assert.Equal(2, admins.Accounts.Select(account => account.Subject).Distinct().Count());
        Assert.Equal(1, admins.MicrosoftIdentityLookupCalls);
        Assert.Equal(0, admins.GenericIdentityLookupCalls);
        Assert.Equal(0, admins.EmailLookupCalls);
    }

    [Fact]
    public async Task Suspended_exact_tuple_is_denied_without_audit()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.Suspend(Guid.NewGuid());
        admins.Add(account);
        var audit = new FakePlatformUserAuditWriter();
        var unitOfWork = new FakeUnitOfWork();

        var result = await Handler(
            admins, new FakeAdminRoleRepository(), audit, unitOfWork).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Suspended, result.Outcome);
        Assert.Single(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.Equal(0, unitOfWork.SaveChangesCalls);
    }

    [Fact]
    public async Task Email_matching_an_unbound_or_non_microsoft_account_is_not_a_resolution_candidate()
    {
        var admins = new FakePlatformUserRepository();
        var historical = User.CreateScoped(Email, Now);
        admins.Add(historical);

        var result = await Handler(
            admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter())
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(2, admins.Accounts.Count);
        Assert.Null(historical.Subject);
        Assert.Null(historical.TenantId);
        Assert.NotEqual(historical.Id, result.Resolution!.AdminId);
        Assert.Equal(0, admins.GenericIdentityLookupCalls);
        Assert.Equal(0, admins.EmailLookupCalls);
    }

    [Fact]
    public async Task Same_object_id_in_a_different_tenant_does_not_resolve_cross_tenant()
    {
        var admins = new FakePlatformUserRepository();
        var foreign = User.JitProvisionMicrosoft(Guid.NewGuid(), ObjectId, Email, Now);
        admins.Add(foreign);

        var result = await Handler(
            admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter())
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.NotEqual(foreign.Id, result.Resolution!.AdminId);
        Assert.Equal(2, admins.Accounts.Count);
        Assert.Equal(1, admins.MicrosoftIdentityLookupCalls);
        Assert.Equal(0, admins.GenericIdentityLookupCalls);
        Assert.Equal(0, admins.EmailLookupCalls);
    }

    [Fact]
    public async Task Identity_inserted_while_waiting_for_lock_is_reused_without_duplicate_audit()
    {
        var existing = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        var admins = new FakePlatformUserRepository
        {
            AfterIdentityMutationLockAcquired = repository => repository.Add(existing),
        };
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, new FakeAdminRoleRepository(), audit).Handle(Command(), default);

        Assert.Equal(existing.Id, result.Resolution!.AdminId);
        Assert.Single(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.Equal(1, admins.IdentityMutationLockCalls);
    }

    [Fact]
    public async Task Unique_conflict_re_resolves_only_the_exact_fresh_tuple()
    {
        var recovery = new FakeRecoveryReader(ResolveResult.Of(
            new Resolution(Guid.NewGuid(), Email, Tier.Scoped, AccessibleMerchants.Of(new HashSet<Guid>()))));
        var handler = Handler(
            new FakePlatformUserRepository(), new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter(),
            new ThrowingUnitOfWork(), recovery);

        var result = await handler.Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(TenantId, recovery.TenantId);
        Assert.Equal(ObjectId, recovery.ObjectId);
    }

    [Fact]
    public async Task Invalid_tuple_or_correlation_is_rejected_before_persistence()
    {
        var admins = new FakePlatformUserRepository();
        var handler = Handler(admins, new FakeAdminRoleRepository(), new FakePlatformUserAuditWriter());

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(Command(tenantId: Guid.Empty), default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(Command(objectId: Guid.Empty), default));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await handler.Handle(new ResolveMicrosoftAdminCommand(
                TenantId, ObjectId, Email, EmployeeId: null, CorrelationId: " "), default));

        Assert.Equal(0, admins.IdentityMutationLockCalls);
        Assert.Equal(0, admins.MicrosoftIdentityLookupCalls);
        Assert.Empty(admins.Accounts);
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
            new ThrowingProfileReader(), unitOfWork ?? new FakeUnitOfWork(), new FixedClock { UtcNow = Now });

    private static ResolveMicrosoftAdminCommand Command(
        Guid? tenantId = null, Guid? objectId = null, string? email = Email) =>
        new(tenantId ?? TenantId, objectId ?? ObjectId, email, EmployeeId: null, "corr-1");

    private sealed class ThrowingProfileReader : IEmployeeProfileReader
    {
        public Task<EmployeeProfileLookup> LookupAsync(string normalizedEmployeeId, CancellationToken ct) =>
            throw new InvalidOperationException("HR source must not be read while the switch is off.");
    }

    private sealed class FakeRecoveryReader(ResolveResult result) : IAdminIdentityRecoveryReader
    {
        public int Calls { get; private set; }
        public Guid? TenantId { get; private set; }
        public Guid? ObjectId { get; private set; }

        public Task<ResolveResult> ResolveAfterConflictAsync(Guid tenantId, Guid objectId, CancellationToken ct)
        {
            Calls++;
            TenantId = tenantId;
            ObjectId = objectId;
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
