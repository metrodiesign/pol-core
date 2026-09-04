using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using SharedKernel;

namespace Admins.Tests;

public sealed class ResolveMicrosoftAdminEmployeeProfileTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string Email = "synthetic@example.test";
    private const string EmployeeId = "ZTEST1";
    private static readonly EmployeeProfile Profile = new("ชื่อทดสอบ", "นามสกุลทดสอบ");

    [Fact]
    public async Task Jit_login_commits_identity_profile_and_two_audits_once()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));
        var uow = new RollbackUnitOfWork(admins, audit);

        var result = await Handler(admins, audit, reader, uow).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        var account = Assert.Single(admins.Accounts);
        Assert.Equal(EmployeeId, account.EmployeeId);
        Assert.Equal(Profile.FirstName, account.FirstName);
        Assert.Equal(Profile.LastName, account.LastName);
        Assert.Equal(Tier.Scoped, account.Tier);
        Assert.Equal([AuditAction.JitProvision, AuditAction.EmployeeBind], audit.Appended.Select(x => x.Action));
        Assert.Equal(1, uow.SaveCalls);
        Assert.Equal([EmployeeId], reader.Lookups);
    }

    [Fact]
    public async Task Existing_first_bind_with_new_names_appends_bind_and_sync_audits()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        admins.Add(account);
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile)))
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal([AuditAction.EmployeeBind, AuditAction.EmployeeProfileSync], audit.Appended.Select(x => x.Action));
    }

    [Fact]
    public async Task Existing_bound_account_refreshes_names_with_one_sync_audit()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile(EmployeeId, "ชื่อเดิม", "นามสกุลเดิม");
        admins.Add(account);
        var version = account.Version;
        var authz = account.AuthorizationVersion;
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile)))
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(Profile.FirstName, account.FirstName);
        Assert.Equal(version + 1, account.Version);
        Assert.Equal(authz, account.AuthorizationVersion);
        Assert.Equal(AuditAction.EmployeeProfileSync, Assert.Single(audit.Appended).Action);
    }

    [Fact]
    public async Task Identical_profile_is_a_no_op_without_profile_audit()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile(EmployeeId, Profile.FirstName, Profile.LastName);
        admins.Add(account);
        var version = account.Version;
        var audit = new FakePlatformUserAuditWriter();

        await Handler(admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile)))
            .Handle(Command(), default);

        Assert.Equal(version, account.Version);
        Assert.Empty(audit.Appended);
    }

    [Fact]
    public async Task Mismatch_denies_before_hr_and_keeps_profile()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile("OTHER", "เดิม", "คงไว้");
        admins.Add(account);
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));

        var result = await Handler(admins, new FakePlatformUserAuditWriter(), reader).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(ResolveResult.EmployeeMismatchReason, result.DenialReason);
        Assert.Equal("OTHER", account.EmployeeId);
        Assert.Empty(reader.Lookups);
    }

    [Fact]
    public async Task Employee_id_owned_by_another_admin_denies_before_hr_and_rolls_back_jit()
    {
        var admins = new FakePlatformUserRepository();
        var owner = User.JitProvisionMicrosoft(TenantId, Guid.NewGuid(), null, Now);
        owner.ApplyEmployeeProfile(EmployeeId, "เจ้าของทดสอบ", "นามสกุลทดสอบ");
        admins.Add(owner);
        var audit = new FakePlatformUserAuditWriter();
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));
        var uow = new RollbackUnitOfWork(admins, audit);

        var result = await Handler(admins, audit, reader, uow).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(ResolveResult.EmployeeTakenReason, result.DenialReason);
        Assert.Same(owner, Assert.Single(admins.Accounts));
        Assert.Empty(audit.Appended);
        Assert.Empty(reader.Lookups);
        Assert.True(uow.RolledBack);
    }

    [Theory]
    [InlineData(EmployeeProfileStatus.Missing, ResolveOutcome.EmployeeProfileMissing, null)]
    [InlineData(EmployeeProfileStatus.Invalid, ResolveOutcome.EmployeeProfileInvalid, null)]
    [InlineData(EmployeeProfileStatus.SourceUnavailable, ResolveOutcome.EmployeeProfileUnavailable, "hr-source-unavailable")]
    public async Task Profile_denial_rolls_back_staged_jit_and_audits(
        EmployeeProfileStatus status,
        ResolveOutcome expected,
        string? reason)
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var uow = new RollbackUnitOfWork(admins, audit);

        var result = await Handler(
                admins, audit, new FakeProfileReader(new EmployeeProfileLookup(status, null)), uow)
            .Handle(Command(), default);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(reason, result.DenialReason);
        Assert.Empty(admins.Accounts);
        Assert.Empty(audit.Appended);
        Assert.True(uow.RolledBack);
        Assert.Equal(0, uow.SaveCalls);
    }

    [Fact]
    public async Task Suspended_exact_identity_denies_before_hr()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.Suspend(Guid.NewGuid());
        admins.Add(account);
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));

        var result = await Handler(admins, new FakePlatformUserAuditWriter(), reader).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Suspended, result.Outcome);
        Assert.Empty(reader.Lookups);
    }

    [Fact]
    public async Task Unique_race_re_runs_once_and_resolves_profile()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var uow = new ConflictUnitOfWork(admins, audit, conflicts: 1);

        var result = await Handler(
                admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile)), uow)
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(2, uow.Attempts);
        Assert.Equal(EmployeeId, Assert.Single(admins.Accounts).EmployeeId);
    }

    [Fact]
    public async Task Second_unique_conflict_is_employee_taken_without_partial_write()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var uow = new ConflictUnitOfWork(admins, audit, conflicts: 2);

        var result = await Handler(
                admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile)), uow)
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(ResolveResult.EmployeeTakenReason, result.DenialReason);
        Assert.Equal(2, uow.Attempts);
        Assert.Empty(admins.Accounts);
        Assert.Empty(audit.Appended);
    }

    [Fact]
    public async Task Switch_off_skips_hr_and_uses_exact_identity_recovery()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));
        var recovery = new CountingRecovery();
        var uow = new ConflictUnitOfWork(admins, audit, conflicts: 1);

        var result = await Handler(admins, audit, reader, uow, recovery)
            .Handle(Command(employeeId: null), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal(1, recovery.Calls);
        Assert.Empty(reader.Lookups);
    }

    private static ResolveMicrosoftAdminHandler Handler(
        FakePlatformUserRepository admins,
        FakePlatformUserAuditWriter audit,
        IEmployeeProfileReader reader,
        IUnitOfWork? unitOfWork = null,
        IAdminIdentityRecoveryReader? recovery = null) =>
        new(admins, new FakeAdminRoleRepository(), audit, recovery ?? new CountingRecovery(), reader,
            unitOfWork ?? new FakeUnitOfWork(), new FixedClock { UtcNow = Now });

    private static ResolveMicrosoftAdminCommand Command(string? employeeId = EmployeeId) =>
        new(TenantId, ObjectId, Email, employeeId, "corr-1");

    private sealed class FakeProfileReader(EmployeeProfileLookup lookup) : IEmployeeProfileReader
    {
        public readonly List<string> Lookups = [];

        public Task<EmployeeProfileLookup> LookupAsync(string normalizedEmployeeId, CancellationToken ct)
        {
            Lookups.Add(normalizedEmployeeId);
            return Task.FromResult(lookup);
        }
    }

    private sealed class CountingRecovery : IAdminIdentityRecoveryReader
    {
        public int Calls { get; private set; }

        public Task<ResolveResult> ResolveAfterConflictAsync(Guid tenantId, Guid objectId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(ResolveResult.IdentityConflict);
        }
    }

    private class RollbackUnitOfWork(FakePlatformUserRepository admins, FakePlatformUserAuditWriter audit) : IUnitOfWork
    {
        public int SaveCalls { get; private set; }
        public bool RolledBack { get; private set; }

        public virtual Task<int> SaveChangesAsync(CancellationToken ct)
        {
            SaveCalls++;
            return Task.FromResult(0);
        }

        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct)
        {
            var accounts = admins.Accounts.ToList();
            var audits = audit.Appended.ToList();
            try
            {
                return await operation(ct);
            }
            catch
            {
                RolledBack = true;
                admins.Accounts.Clear();
                admins.Accounts.AddRange(accounts);
                audit.Appended.Clear();
                audit.Appended.AddRange(audits);
                throw;
            }
        }
    }

    private sealed class ConflictUnitOfWork(
        FakePlatformUserRepository admins,
        FakePlatformUserAuditWriter audit,
        int conflicts) : RollbackUnitOfWork(admins, audit)
    {
        public int Attempts { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken ct)
        {
            Attempts++;
            if (Attempts <= conflicts)
                throw new ConflictException("synthetic unique conflict");
            return base.SaveChangesAsync(ct);
        }
    }
}
