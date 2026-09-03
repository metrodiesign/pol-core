using Admins.Application.Users;
using Admins.Domain.Users;
using BuildingBlocks.Application;
using SharedKernel;

namespace Admins.Tests;

/// <summary>tier0-graph-employee-profile task 3: <see cref="ResolveMicrosoftAdminHandler"/> with the switch ON
/// (EmployeeId present) — ordering (exact tuple outcomes before HR), mismatch/taken, every denial rolling staged
/// JIT/profile work back, Inactive same-vs-different, single employee-bind audit, unique-race re-run (REQ-2.5-2.14, 2.17,
/// 3.19, 4.11, 5.7, 7.1-7.9, 7.14-7.17, 10.2-10.5, 12.4-12.6).</summary>
public sealed class ResolveMicrosoftAdminEmployeeProfileTests
{
    private static readonly DateTime Now = new(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid ObjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private const string Email = "employee@viriyah.co.th";
    private const string EmployeeId = "ZTEST1";
    private static readonly Guid Office = Guid.NewGuid();
    private static readonly Guid Division = Guid.NewGuid();
    private static readonly EmployeeProfile Profile = new("สมชาย", "ใจดี", Office, true, Division, true);

    [Fact]
    public async Task Jit_login_creates_the_account_with_profile_and_two_audits_in_one_commit()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));
        var uow = new RollbackUnitOfWork(admins, audit);

        var result = await Handler(admins, audit, reader, uow).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        var account = Assert.Single(admins.Accounts);
        Assert.Equal(EmployeeId, account.EmployeeId);
        Assert.Equal("สมชาย", account.FirstName);
        Assert.Equal("ใจดี", account.LastName);
        Assert.Equal(Office, account.OfficeId);
        Assert.Equal(Division, account.DivisionId);
        Assert.Equal(Tier.Scoped, account.Tier);
        Assert.Equal([AuditAction.JitProvision, AuditAction.EmployeeBind], audit.Appended.Select(a => a.Action));
        Assert.Equal(1, uow.SaveCalls);
        Assert.Equal([EmployeeId], reader.Lookups);
    }

    [Fact]
    public async Task Existing_bound_account_refreshes_profile_without_a_second_bind_audit()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile(EmployeeId, "เก่า", "เก่า", Guid.NewGuid(), Guid.NewGuid());
        admins.Add(account);
        var version = account.Version;
        var authz = account.AuthorizationVersion;
        var audit = new FakePlatformUserAuditWriter();

        var result = await Handler(admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile))).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal("สมชาย", account.FirstName);
        Assert.Equal(Office, account.OfficeId);
        Assert.Equal(version + 1, account.Version);
        Assert.Equal(authz, account.AuthorizationVersion);
        Assert.Empty(audit.Appended);
    }

    [Fact]
    public async Task Identical_profile_on_existing_account_does_not_bump_version()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile(EmployeeId, Profile.FirstName, Profile.LastName, Office, Division);
        admins.Add(account);
        var version = account.Version;

        await Handler(admins, new FakePlatformUserAuditWriter(), new FakeProfileReader(EmployeeProfileLookup.Found(Profile)))
            .Handle(Command(), default);

        Assert.Equal(version, account.Version);
    }

    [Fact]
    public async Task Lowercase_graph_id_is_normalised_upstream_so_the_handler_compares_ordinally()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile(EmployeeId, "a", "b", Office, Division);
        admins.Add(account);

        // REQ-2.7: an ordinal-equal id resolves; the host normalises before the command is built.
        var result = await Handler(admins, new FakePlatformUserAuditWriter(), new FakeProfileReader(EmployeeProfileLookup.Found(Profile)))
            .Handle(Command(employeeId: EmployeeId), default);
        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
    }

    [Fact]
    public async Task Different_bound_employee_id_is_an_identity_conflict_with_mismatch_reason_and_no_overwrite()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile("OTHER", "a", "b", Office, Division);
        admins.Add(account);
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));

        var result = await Handler(admins, new FakePlatformUserAuditWriter(), reader).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal("employee-mismatch", result.DenialReason);
        Assert.Equal("OTHER", account.EmployeeId);
        Assert.Equal("a", account.FirstName);
        Assert.Empty(reader.Lookups); // mismatch is decided before the HR read
    }

    [Fact]
    public async Task Employee_id_held_by_another_admin_is_taken_and_the_staged_jit_is_rolled_back()
    {
        var admins = new FakePlatformUserRepository();
        var other = User.JitProvisionMicrosoft(TenantId, Guid.NewGuid(), "other@viriyah.co.th", Now);
        other.ApplyEmployeeProfile(EmployeeId, "a", "b", Office, Division);
        admins.Add(other);
        var audit = new FakePlatformUserAuditWriter();
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));
        var uow = new RollbackUnitOfWork(admins, audit);

        var result = await Handler(admins, audit, reader, uow).Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal("employee-taken", result.DenialReason);
        Assert.Single(admins.Accounts);   // no JIT survived
        Assert.Empty(audit.Appended);     // no JIT audit survived
        Assert.Empty(reader.Lookups);
        Assert.Equal(0, uow.SaveCalls);
        Assert.True(uow.RolledBack);
    }

    [Fact]
    public async Task Account_holding_the_id_itself_is_not_taken()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        account.ApplyEmployeeProfile(EmployeeId, "a", "b", Office, Division);
        admins.Add(account);

        var result = await Handler(admins, new FakePlatformUserAuditWriter(), new FakeProfileReader(EmployeeProfileLookup.Found(Profile)))
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
    }

    [Theory]
    [InlineData(EmployeeProfileStatus.Missing, ResolveOutcome.EmployeeProfileMissing, null)]
    [InlineData(EmployeeProfileStatus.Invalid, ResolveOutcome.EmployeeProfileInvalid, null)]
    [InlineData(EmployeeProfileStatus.Unmapped, ResolveOutcome.EmployeeProfileUnmapped, null)]
    [InlineData(EmployeeProfileStatus.SourceUnavailable, ResolveOutcome.EmployeeProfileUnavailable, "hr-source-unavailable")]
    public async Task Reader_denial_maps_to_outcome_and_rolls_back_the_staged_exact_tuple_and_audits(
        EmployeeProfileStatus status, ResolveOutcome expected, string? reason)
    {
        var admins = new FakePlatformUserRepository();
        var unrelated = User.JitProvisionMicrosoft(TenantId, Guid.NewGuid(), Email, Now);
        admins.Add(unrelated);
        var audit = new FakePlatformUserAuditWriter();
        var uow = new RollbackUnitOfWork(admins, audit);

        var result = await Handler(admins, audit, new FakeProfileReader(new EmployeeProfileLookup(status, null)), uow)
            .Handle(Command(), default);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(reason, result.DenialReason);
        Assert.Null(result.Resolution);
        Assert.True(uow.RolledBack);
        Assert.Equal(0, uow.SaveCalls);
        Assert.Empty(audit.Appended);
        Assert.Same(unrelated, Assert.Single(admins.Accounts));
        Assert.DoesNotContain(admins.Accounts, account => account.Subject == ObjectId.ToString("D"));
    }

    [Fact]
    public async Task Inactive_office_that_differs_from_current_is_unmapped_but_same_value_resolves()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        admins.Add(account);
        var inactiveOffice = Profile with { OfficeActive = false };

        var denied = await Handler(admins, new FakePlatformUserAuditWriter(), new FakeProfileReader(EmployeeProfileLookup.Found(inactiveOffice)))
            .Handle(Command(), default);
        Assert.Equal(ResolveOutcome.EmployeeProfileUnmapped, denied.Outcome);

        account.UpdateProfile(null, Office, null, Division);
        var resolved = await Handler(admins, new FakePlatformUserAuditWriter(), new FakeProfileReader(EmployeeProfileLookup.Found(inactiveOffice)))
            .Handle(Command(), default);
        Assert.Equal(ResolveOutcome.Resolved, resolved.Outcome);
        Assert.Equal(Office, account.OfficeId);
    }

    [Fact]
    public async Task Inactive_division_that_differs_from_current_is_unmapped_but_same_value_resolves()
    {
        var admins = new FakePlatformUserRepository();
        var account = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        admins.Add(account);
        var inactiveDivision = Profile with { DivisionActive = false };

        Assert.Equal(ResolveOutcome.EmployeeProfileUnmapped,
            (await Handler(admins, new FakePlatformUserAuditWriter(), new FakeProfileReader(EmployeeProfileLookup.Found(inactiveDivision)))
                .Handle(Command(), default)).Outcome);

        account.UpdateProfile(null, null, null, Division);
        Assert.Equal(ResolveOutcome.Resolved,
            (await Handler(admins, new FakePlatformUserAuditWriter(), new FakeProfileReader(EmployeeProfileLookup.Found(inactiveDivision)))
                .Handle(Command(), default)).Outcome);
    }

    [Fact]
    public async Task Suspended_exact_identity_is_decided_before_any_hr_read()
    {
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));
        var admins = new FakePlatformUserRepository();
        var suspended = User.JitProvisionMicrosoft(TenantId, ObjectId, Email, Now);
        suspended.Suspend(Guid.NewGuid());
        admins.Add(suspended);

        Assert.Equal(ResolveOutcome.Suspended,
            (await Handler(admins, new FakePlatformUserAuditWriter(), reader).Handle(Command(), default)).Outcome);
        Assert.Empty(reader.Lookups);
    }

    [Fact]
    public async Task Unique_race_re_runs_once_and_resolves_with_profile()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var recovery = new CountingRecovery();
        var uow = new ConflictOnceUnitOfWork(admins, audit);

        var result = await Handler(admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile)), uow, recovery)
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.Resolved, result.Outcome);
        Assert.Equal(2, uow.Attempts);
        Assert.Equal(0, recovery.Calls); // switch on: the read-only recovery reader is never used
        var account = Assert.Single(admins.Accounts);
        Assert.Equal(EmployeeId, account.EmployeeId);
    }

    [Fact]
    public async Task Second_unique_conflict_is_employee_taken()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var uow = new ConflictOnceUnitOfWork(admins, audit) { ConflictsToRaise = 2 };

        var result = await Handler(admins, audit, new FakeProfileReader(EmployeeProfileLookup.Found(Profile)), uow)
            .Handle(Command(), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome);
        Assert.Equal("employee-taken", result.DenialReason);
        Assert.Equal(2, uow.Attempts);
    }

    [Fact]
    public async Task Switch_off_never_reads_hr_and_uses_recovery_on_conflict()
    {
        var admins = new FakePlatformUserRepository();
        var audit = new FakePlatformUserAuditWriter();
        var reader = new FakeProfileReader(EmployeeProfileLookup.Found(Profile));
        var recovery = new CountingRecovery();
        var uow = new ConflictOnceUnitOfWork(admins, audit);

        var result = await Handler(admins, audit, reader, uow, recovery).Handle(Command(employeeId: null), default);

        Assert.Equal(ResolveOutcome.IdentityConflict, result.Outcome); // recovery returns IdentityConflict
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(1, uow.Attempts);
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

    /// <summary>Mirrors ControlPlaneUnitOfWork: a throwing operation rolls back — here by restoring the in-memory
    /// repository/audit lists to their pre-transaction snapshot (the real one clears the change tracker).</summary>
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

    private sealed class ConflictOnceUnitOfWork(FakePlatformUserRepository admins, FakePlatformUserAuditWriter audit)
        : RollbackUnitOfWork(admins, audit)
    {
        public int ConflictsToRaise { get; init; } = 1;
        public int Attempts { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken ct)
        {
            Attempts++;
            if (Attempts <= ConflictsToRaise)
                throw new ConflictException("unique conflict");
            return base.SaveChangesAsync(ct);
        }
    }
}
