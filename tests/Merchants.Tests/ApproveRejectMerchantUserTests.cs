using BuildingBlocks.Application;
using Merchants.Application;
using Merchants.Application.Users;
using Merchants.Application.Users.Roles;
using Merchants.Domain;
using Merchants.Domain.Users;
using Merchants.Domain.Users.Roles;
using SharedKernel;

namespace Merchants.Tests;

/// <summary>
/// Admin approve/reject of a merchant user (REQ-6): approve binds the validated merchant + assigns the roles +
/// activates, is idempotent for an already-Active target on the SAME merchant (REQ-6.4), rejects a different-merchant
/// re-approve (409 — MerchantUser.Approve itself enforces the merchant-match guard now that the former separate
/// assignment row is absorbed into MerchantUser.MerchantId), rejects an unknown target (404), a non-Pending target
/// (409), an empty role set (400), and an unknown/inactive role (409). Reject flips Pending→Rejected, revokes the
/// user's live sessions (REQ-12.3), and rejects an unknown (404) or non-Pending (409) target. The Admin permission +
/// accessible-merchant floor are the HOST's job (B3) — the command trusts the already-validated merchant id.
/// </summary>
public sealed class ApproveRejectMerchantUserTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Merchant = Guid.Parse("d2222222-2222-2222-2222-222222222222");
    private static readonly Guid AdminId = Guid.Parse("ad000000-0000-0000-0000-0000000000a1");

    // --- approve ---

    [Fact]
    public async Task Approve_unknown_target_is_404() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Approve(users: new FakeUsers(), "merchant_member"));

    [Fact]
    public async Task Approve_a_non_pending_target_is_409()
    {
        var users = new FakeUsers();
        var u = Pending(); u.Reject(Now); // Rejected
        users.Seed(u);
        var roles = new FakeRoles(); roles.SeedActive("merchant_member");
        await Assert.ThrowsAsync<ConflictException>(() => Approve(users, "merchant_member", roles, u.Id));
    }

    [Fact]
    public async Task Approve_an_already_active_target_on_the_same_merchant_is_an_idempotent_no_op()
    {
        var users = new FakeUsers();
        var u = Pending(); u.Approve(Merchant, Now); // already Active, bound to Merchant
        users.Seed(u);
        var roles = new FakeRoles(); roles.SeedActive("merchant_member");

        var result = await Approve(users, "merchant_member", roles, u.Id);

        Assert.True(result.AlreadyActive);
        Assert.Empty(roles.Assignments);      // no re-assignment (REQ-6.4)
    }

    [Fact]
    public async Task Approve_an_already_active_target_on_a_different_merchant_is_409()
    {
        var users = new FakeUsers();
        var u = Pending(); u.Approve(Guid.NewGuid(), Now); users.Seed(u); // bound to a DIFFERENT merchant
        var roles = new FakeRoles(); roles.SeedActive("merchant_member");

        // MerchantUser.Approve itself throws InvalidOperationException, mapped 409 by the host — the handler
        // does not catch it, so the assertion is on the domain-level exception type here.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Approve(users, "merchant_member", roles, u.Id));
    }

    [Fact]
    public async Task Approve_with_no_roles_is_400()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        await Assert.ThrowsAsync<ArgumentException>(() => Approve(users, roleCodes: [], merchantUserId: u.Id));
    }

    [Fact]
    public async Task Approve_with_an_unknown_or_inactive_role_is_409()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var roles = new FakeRoles(); roles.SeedInactive("retired_role");

        await Assert.ThrowsAsync<ConflictException>(() => Approve(users, "ghost", roles, u.Id));      // unknown
        await Assert.ThrowsAsync<ConflictException>(() => Approve(users, "retired_role", roles, u.Id)); // inactive
    }

    [Fact]
    public async Task Approve_happy_path_activates_assigns_roles_and_audits()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var roles = new FakeRoles(); var member = roles.SeedActive("merchant_member");
        var audit = new FakeAudit();

        var result = await Approve(users, "merchant_member", roles, u.Id, audit);

        Assert.False(result.AlreadyActive);
        Assert.Equal(UserStatus.Active, u.Status);
        Assert.Equal(Merchant, u.MerchantId);
        var assignment = Assert.Single(roles.Assignments);
        Assert.Equal(member, assignment.RoleId);
        Assert.Equal(Merchant, assignment.MerchantId);
        Assert.Equal(AdminId, assignment.AssignedById);
        Assert.Contains(audit.Rows, a => a.Action == RegistrationAuditAction.Approved
            && a.TargetSubject == u.Subject && a.ActorAdminId == AdminId);
    }

    // --- reject ---

    [Fact]
    public async Task Reject_unknown_target_is_404() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Reject(new FakeUsers(), new FakeSessions()));

    [Fact]
    public async Task Reject_a_non_pending_target_is_409()
    {
        var users = new FakeUsers(); var u = Pending(); u.Approve(Merchant, Now); users.Seed(u);
        await Assert.ThrowsAsync<ConflictException>(() => Reject(users, new FakeSessions(), u.Id));
    }

    [Fact]
    public async Task Reject_happy_path_sets_rejected_revokes_sessions_and_audits()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var sessions = new FakeSessions();
        var audit = new FakeAudit();

        await Reject(users, sessions, u.Id, audit, reason: "Incomplete tax documents");

        Assert.Equal(UserStatus.Rejected, u.Status);
        Assert.Equal(u.Id, sessions.RevokedUser);
        var row = Assert.Single(audit.Rows, a => a.Action == RegistrationAuditAction.Rejected && a.TargetSubject == u.Subject);
        Assert.Equal("Incomplete tax documents", row.Reason); // REQ-5.1: the rationale is recorded
        Assert.Equal(AdminId, row.ActorAdminId);
    }

    [Fact]
    public async Task Reject_with_a_blank_reason_records_null_not_empty()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var audit = new FakeAudit();

        await Reject(users, new FakeSessions(), u.Id, audit, reason: "   ");

        Assert.Null(Assert.Single(audit.Rows).Reason);
    }

    [Fact]
    public void An_admin_audit_action_requires_the_canonical_actor_id()
    {
        var error = Assert.Throws<ArgumentException>(() => RegistrationAudit.For(
            RegistrationAuditAction.Rejected, Guid.NewGuid(), "merchant-sub", "corr", Now));

        Assert.Equal("actorAdminId", error.ParamName);
    }

    // --- harness ---

    private static User Pending() => User.Register("google", "google-sub-" + Guid.NewGuid().ToString("N")[..6], "p@org.com", Now);

    private static Task<ApproveResult> Approve(
        FakeUsers users, string roleCode, FakeRoles? roles = null, Guid? merchantUserId = null, FakeAudit? audit = null) =>
        Approve(users, [roleCode], merchantUserId, audit, roles);

    private static Task<ApproveResult> Approve(
        FakeUsers users, IReadOnlyList<string> roleCodes, Guid? merchantUserId, FakeAudit? audit = null, FakeRoles? roles = null) =>
        new ApproveHandler(users, roles ?? new FakeRoles(), audit ?? new FakeAudit(), new FakeUow(), new FakeClock())
            .Handle(new ApproveCommand(merchantUserId ?? Guid.NewGuid(), Merchant, roleCodes, "admin-sub", AdminId, "corr"), default).AsTask();

    private static Task<RejectResult> Reject(
        FakeUsers users, FakeSessions sessions, Guid? merchantUserId = null, FakeAudit? audit = null, string? reason = "reason") =>
        new RejectHandler(users, sessions, audit ?? new FakeAudit(), new FakeUow(), new FakeClock())
            .Handle(new RejectCommand(merchantUserId ?? Guid.NewGuid(), reason, "admin-sub", "corr", AdminId), default).AsTask();

    private sealed class FakeUow : IUserUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct) => op(ct);
    }

    private sealed class FakeClock : IClock { public DateTime UtcNow => Now; }

    private sealed class FakeUsers : IAccountStore
    {
        private readonly Dictionary<Guid, User> _byId = [];
        public void Seed(User u) => _byId[u.Id] = u;
        public Task<User?> FindByIdentityAsync(ProviderIdentity identity, CancellationToken ct) =>
            Task.FromResult(_byId.Values.FirstOrDefault(u => u.Provider == identity.Provider && u.Subject == identity.Subject));
        public Task<User?> FindByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(_byId.GetValueOrDefault(id));
        public void Add(User account) => throw new NotSupportedException();
    }

    private sealed class FakeAudit : IRegistrationAuditWriter
    {
        public readonly List<RegistrationAudit> Rows = [];
        public void Append(RegistrationAudit audit) => Rows.Add(audit);
    }

    private sealed class FakeSessions : ISessionStore
    {
        public Guid? RevokedUser;
        public Task RevokeAllForUserAsync(Guid merchantUserId, CancellationToken ct) { RevokedUser = merchantUserId; return Task.CompletedTask; }
        // Unused.
        public Task<Session?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) => throw new NotSupportedException();
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => throw new NotSupportedException();
        public void Add(Session session) => throw new NotSupportedException();
        public Task<int> SaveChangesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeRoles : IRoleRepository
    {
        // Only ACTIVE codes land here — GetActiveRoleIdsByCodesAsync (REQ-6.5) collapses "unknown" and
        // "inactive" into the same absent-from-result case, so an inactive seed need not be tracked at all.
        private readonly Dictionary<string, Guid> _active = [];
        public readonly List<RoleAssignment> Assignments = [];

        public Guid SeedActive(string code)
        {
            var id = Guid.NewGuid();
            _active[code] = id;
            return id;
        }

        public Guid SeedInactive(string code) => Guid.NewGuid(); // never resolvable — absent from _active

        public void AddAssignment(RoleAssignment assignment) => Assignments.Add(assignment);

        public Task<IReadOnlyDictionary<string, Guid>> GetActiveRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                _active.Where(kv => codes.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value));

        // Unused by approve/reject.
        public void RemoveAssignment(RoleAssignment assignment) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(
            Guid merchantId, IReadOnlyCollection<string> codes, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid merchantUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task<RoleAssignment?> GetAssignmentAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AssignmentExistsAsync(Guid merchantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid merchantUserId, Guid merchantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
