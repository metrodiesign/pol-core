using BuildingBlocks.Application;
using Producer.Application;
using Producer.Domain;

namespace Producer.Tests;

/// <summary>
/// Admin approve/reject of a producer (REQ-6): approve binds the validated tenant + assigns the roles + activates,
/// is idempotent for an already-Active target (REQ-6.4), rejects an unknown target (404), a non-Pending target
/// (409), an empty role set (400), and an unknown/inactive role (409). Reject flips Pending→Rejected, revokes the
/// user's live sessions (REQ-12.3), and rejects an unknown (404) or non-Pending (409) target. The Admin permission +
/// accessible-tenant floor are the HOST's job (B3) — the command trusts the already-validated tenant id.
/// </summary>
public sealed class ProducerApproveRejectHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.Parse("d2222222-2222-2222-2222-222222222222");
    private static readonly Guid AdminId = Guid.Parse("ad000000-0000-0000-0000-0000000000a1");

    // --- approve ---

    [Fact]
    public async Task Approve_unknown_target_is_404() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Approve(users: new FakeUsers(), "tenant_member"));

    [Fact]
    public async Task Approve_a_non_pending_target_is_409()
    {
        var users = new FakeUsers();
        var u = Pending(); u.Reject(Now); // Rejected
        users.Seed(u);
        var roles = new FakeRoles(); roles.SeedActive("tenant_member");
        await Assert.ThrowsAsync<ConflictException>(() => Approve(users, "tenant_member", roles, u.Subject));
    }

    [Fact]
    public async Task Approve_an_already_active_target_on_the_same_tenant_is_an_idempotent_no_op()
    {
        var users = new FakeUsers();
        var u = Pending(); u.Approve(Now); // already Active
        users.Seed(u);
        var assignments = new FakeAssignments();
        assignments.Seed(ProducerTenantAssignment.Create(u.Id, Tenant, AdminId, Now)); // bound to the SAME tenant
        var roles = new FakeRoles(); roles.SeedActive("tenant_member");

        var result = await Approve(users, "tenant_member", roles, u.Subject, assignments: assignments);

        Assert.True(result.AlreadyActive);
        Assert.Empty(roles.Assignments);      // no re-assignment (REQ-6.4)
        Assert.Empty(assignments.Added);      // no duplicate tenant edge
    }

    [Fact]
    public async Task Approve_an_already_active_target_on_a_different_tenant_is_409()
    {
        var users = new FakeUsers();
        var u = Pending(); u.Approve(Now); users.Seed(u);
        var assignments = new FakeAssignments();
        assignments.Seed(ProducerTenantAssignment.Create(u.Id, Guid.NewGuid(), AdminId, Now)); // a DIFFERENT tenant
        var roles = new FakeRoles(); roles.SeedActive("tenant_member");

        await Assert.ThrowsAsync<ConflictException>(() => Approve(users, "tenant_member", roles, u.Subject, assignments: assignments));
    }

    [Fact]
    public async Task Approve_with_no_roles_is_400()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        await Assert.ThrowsAsync<ArgumentException>(() => Approve(users, roleCodes: [], subject: u.Subject));
    }

    [Fact]
    public async Task Approve_with_an_unknown_or_inactive_role_is_409()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var roles = new FakeRoles(); roles.SeedInactive("retired_role");

        await Assert.ThrowsAsync<ConflictException>(() => Approve(users, "ghost", roles, u.Subject));      // unknown
        await Assert.ThrowsAsync<ConflictException>(() => Approve(users, "retired_role", roles, u.Subject)); // inactive
    }

    [Fact]
    public async Task Approve_happy_path_activates_assigns_roles_and_audits()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var roles = new FakeRoles(); var member = roles.SeedActive("tenant_member");
        var audit = new FakeAudit();

        var assignments = new FakeAssignments();
        var result = await Approve(users, "tenant_member", roles, u.Subject, audit, assignments: assignments);

        Assert.False(result.AlreadyActive);
        Assert.Equal(ProducerAccountStatus.Active, u.Status);
        var tenantEdge = Assert.Single(assignments.Added);
        Assert.Equal(Tenant, tenantEdge.TenantId);
        Assert.Equal(AdminId, tenantEdge.AssignedByAdminId);
        var assignment = Assert.Single(roles.Assignments);
        Assert.Equal(member.Id, assignment.RoleId);
        Assert.Equal(Tenant, assignment.TenantId);
        Assert.Equal(AdminId, assignment.AssignedByAdminId);
        Assert.Contains(audit.Rows, a => a.Action == RegistrationAuditAction.Approved && a.TargetSubject == u.Subject);
    }

    // --- reject ---

    [Fact]
    public async Task Reject_unknown_target_is_404() =>
        await Assert.ThrowsAsync<NotFoundException>(() => Reject(new FakeUsers(), new FakeSessions()));

    [Fact]
    public async Task Reject_a_non_pending_target_is_409()
    {
        var users = new FakeUsers(); var u = Pending(); u.Approve(Now); users.Seed(u);
        await Assert.ThrowsAsync<ConflictException>(() => Reject(users, new FakeSessions(), u.Subject));
    }

    [Fact]
    public async Task Reject_happy_path_sets_rejected_revokes_sessions_and_audits()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var sessions = new FakeSessions();
        var audit = new FakeAudit();

        await Reject(users, sessions, u.Subject, audit, reason: "Incomplete tax documents");

        Assert.Equal(ProducerAccountStatus.Rejected, u.Status);
        Assert.Equal(u.Id, sessions.RevokedUser);
        var row = Assert.Single(audit.Rows, a => a.Action == RegistrationAuditAction.Rejected && a.TargetSubject == u.Subject);
        Assert.Equal("Incomplete tax documents", row.Reason); // REQ-5.1: the rationale is recorded
    }

    [Fact]
    public async Task Reject_with_a_blank_reason_records_null_not_empty()
    {
        var users = new FakeUsers(); var u = Pending(); users.Seed(u);
        var audit = new FakeAudit();

        await Reject(users, new FakeSessions(), u.Subject, audit, reason: "   ");

        Assert.Null(Assert.Single(audit.Rows).Reason);
    }

    // --- harness ---

    private static ProducerAccount Pending() => ProducerAccount.Register("google-sub-" + Guid.NewGuid().ToString("N")[..6], "p@org.com", Now);

    private static Task<ApproveTenantUserResult> Approve(
        FakeUsers users, string roleCode, FakeRoles? roles = null, string subject = "google-sub", FakeAudit? audit = null,
        FakeAssignments? assignments = null) =>
        Approve(users, [roleCode], subject, audit, roles, assignments);

    private static Task<ApproveTenantUserResult> Approve(
        FakeUsers users, IReadOnlyList<string> roleCodes, string subject, FakeAudit? audit = null, FakeRoles? roles = null,
        FakeAssignments? assignments = null) =>
        new ApproveTenantUserHandler(users, assignments ?? new FakeAssignments(), roles ?? new FakeRoles(),
                audit ?? new FakeAudit(), new FakeUow(), new FakeClock())
            .Handle(new ApproveTenantUserCommand(subject, Tenant, roleCodes, "admin-sub", AdminId, "corr"), default).AsTask();

    private static Task<RejectTenantUserResult> Reject(
        FakeUsers users, FakeSessions sessions, string subject = "google-sub", FakeAudit? audit = null, string? reason = "reason") =>
        new RejectTenantUserHandler(users, sessions, audit ?? new FakeAudit(), new FakeUow(), new FakeClock())
            .Handle(new RejectTenantUserCommand(subject, reason, "admin-sub", "corr"), default).AsTask();

    private sealed class FakeUow : IProducerUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct) => op(ct);
    }

    private sealed class FakeClock : IClock { public DateTime UtcNow => Now; }

    private sealed class FakeUsers : IProducerAccountRepository
    {
        private readonly Dictionary<string, ProducerAccount> _bySubject = [];
        public void Seed(ProducerAccount u) => _bySubject[u.Subject] = u;
        public Task<ProducerAccount?> FindBySubjectAsync(string subject, CancellationToken ct) => Task.FromResult(_bySubject.GetValueOrDefault(subject));
        public Task<ProducerAccount?> FindByIdAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public void Add(ProducerAccount account) => throw new NotSupportedException();
    }

    private sealed class FakeAssignments : IProducerTenantAssignmentRepository
    {
        private readonly Dictionary<Guid, ProducerTenantAssignment> _byAccount = [];
        public readonly List<ProducerTenantAssignment> Added = [];
        public void Seed(ProducerTenantAssignment a) => _byAccount[a.ProducerAccountId] = a;
        public Task<ProducerTenantAssignment?> FindByAccountIdAsync(Guid producerAccountId, CancellationToken ct) =>
            Task.FromResult(_byAccount.GetValueOrDefault(producerAccountId));
        public void Add(ProducerTenantAssignment a) { Added.Add(a); _byAccount[a.ProducerAccountId] = a; }
    }

    private sealed class FakeAudit : IRegistrationAuditWriter
    {
        public readonly List<RegistrationAudit> Rows = [];
        public void Append(RegistrationAudit audit) => Rows.Add(audit);
    }

    private sealed class FakeSessions : IProducerSessionStore
    {
        public Guid? RevokedUser;
        public Task RevokeAllForUserAsync(Guid tenantUserId, CancellationToken ct) { RevokedUser = tenantUserId; return Task.CompletedTask; }
        // Unused.
        public Task<ProducerSession?> FindByTokenHashAsync(byte[] hash, CancellationToken ct) => throw new NotSupportedException();
        public Task<Guid?> GetFamilyActiveSessionIdAsync(Guid familyId, CancellationToken ct) => throw new NotSupportedException();
        public void Add(ProducerSession session) => throw new NotSupportedException();
        public Task<int> SaveChangesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> TrySupersedeAsync(Guid id, Guid succ, DateTime now, CancellationToken ct) => throw new NotSupportedException();
        public Task SlideIdleAsync(Guid id, DateTime idle, CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeFamilyAsync(Guid familyId, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> PruneAsync(DateTime now, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeRoles : IProducerRoleRepository
    {
        private readonly Dictionary<string, ProducerRole> _byCode = [];
        public readonly List<ProducerRoleAssignment> Assignments = [];

        public ProducerRole SeedActive(string code) => Seed(code, ProducerRoleStatus.Active);
        public ProducerRole SeedInactive(string code) => Seed(code, ProducerRoleStatus.Inactive);
        private ProducerRole Seed(string code, ProducerRoleStatus status)
        {
            var role = ProducerRole.Create(code, code, null, null, status, [], ProducerPermissions.AllKeys);
            _byCode[code] = role;
            return role;
        }

        public Task<ProducerRole?> GetByCodeAsync(string code, CancellationToken ct) => Task.FromResult(_byCode.GetValueOrDefault(code));
        public void AddAssignment(ProducerRoleAssignment assignment) => Assignments.Add(assignment);

        // Unused by approve/reject.
        public void Add(ProducerRole role) => throw new NotSupportedException();
        public void Remove(ProducerRole role) => throw new NotSupportedException();
        public void RemoveAssignment(ProducerRoleAssignment assignment) => throw new NotSupportedException();
        public Task<bool> CodeExistsAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProducerRoleListItem>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid tenantUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerRoleAssignment?> GetAssignmentAsync(Guid tenantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AssignmentExistsAsync(Guid tenantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerPermissionCatalogResult> ListCatalogAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid tenantUserId, Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid tenantUserId, Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
