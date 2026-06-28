using BuildingBlocks.Application;
using Producer.Application;
using Producer.Domain;

namespace Producer.Tests;

/// <summary>
/// The producer role-management handler branches (REQ-16): create rejects a duplicate code (409); update/delete
/// reject the <c>tenant_owner</c> anchor (409); delete rejects a role with bound assignments (409); and the
/// genuinely-new <c>SetProducerUserRoles</c> tenant scoping — a target outside the acting tenant (or not Active) is
/// invisible (404, no leak), an unknown role code is 400, and an in-tenant Active target's assignments are set to
/// exactly the requested set.
/// </summary>
public sealed class ProducerRoleHandlerTests
{
    private static readonly DateTime Now = new(2026, 6, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantA = Guid.Parse("a0000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TenantB = Guid.Parse("b0000000-0000-0000-0000-0000000000b1");
    private static readonly Guid Actor = Guid.Parse("ac000000-0000-0000-0000-0000000000c1");

    [Fact]
    public async Task Create_rejects_a_duplicate_code()
    {
        var roles = new FakeRoles();
        roles.SeedRole("tenant_member", ProducerRoleStatus.Active);
        var handler = new CreateProducerRoleHandler(roles, new FakeUow());

        await Assert.ThrowsAsync<ConflictException>(() => handler.Handle(
            new CreateProducerRoleCommand("tenant_member", "dup", null, null, ProducerRoleStatus.Active, ["product.create"]), default).AsTask());
    }

    [Fact]
    public async Task Create_rejects_a_permission_key_outside_the_catalog()
    {
        var roles = new FakeRoles();
        var handler = new CreateProducerRoleHandler(roles, new FakeUow());

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(
            new CreateProducerRoleCommand("ops", "Ops", null, null, ProducerRoleStatus.Active, ["bogus.key"]), default).AsTask());
    }

    [Fact]
    public async Task Update_rejects_deactivating_the_tenant_owner_anchor()
    {
        var roles = new FakeRoles();
        roles.SeedRole(ProducerRole.TenantOwnerCode, ProducerRoleStatus.Active);
        var handler = new UpdateProducerRoleHandler(roles, new FakeUow());

        await Assert.ThrowsAsync<ConflictException>(() => handler.Handle(
            new UpdateProducerRoleCommand(ProducerRole.TenantOwnerCode, "Owner", null, null, ProducerRoleStatus.Inactive, []), default).AsTask());
    }

    [Fact]
    public async Task Delete_rejects_the_anchor_and_a_role_with_bound_users()
    {
        var roles = new FakeRoles();
        roles.SeedRole(ProducerRole.TenantOwnerCode, ProducerRoleStatus.Active);
        var member = roles.SeedRole("tenant_member", ProducerRoleStatus.Active);
        roles.Assignments.Add(ProducerRoleAssignment.Create(Guid.NewGuid(), member.Id, TenantA, Actor, Now));
        var handler = new DeleteProducerRoleHandler(roles, new FakeUow());

        await Assert.ThrowsAsync<ConflictException>(() => handler.Handle(new DeleteProducerRoleCommand(ProducerRole.TenantOwnerCode), default).AsTask());
        await Assert.ThrowsAsync<ConflictException>(() => handler.Handle(new DeleteProducerRoleCommand("tenant_member"), default).AsTask());
    }

    [Fact]
    public async Task SetUserRoles_hides_a_target_outside_the_acting_tenant()
    {
        var users = new FakeUsers();
        var target = Approved(TenantB); // different tenant
        users.Seed(target);
        var roles = new FakeRoles();
        roles.SeedRole("tenant_member", ProducerRoleStatus.Active);
        var handler = new SetProducerUserRolesHandler(users, roles, new FakeUow(), new FakeClock());

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(
            new SetProducerUserRolesCommand(target.Id, ["tenant_member"], TenantA, Actor), default).AsTask());
    }

    [Fact]
    public async Task SetUserRoles_rejects_an_unknown_role_code()
    {
        var users = new FakeUsers();
        var target = Approved(TenantA);
        users.Seed(target);
        var handler = new SetProducerUserRolesHandler(users, new FakeRoles(), new FakeUow(), new FakeClock());

        await Assert.ThrowsAsync<ArgumentException>(() => handler.Handle(
            new SetProducerUserRolesCommand(target.Id, ["ghost_role"], TenantA, Actor), default).AsTask());
    }

    [Fact]
    public async Task SetUserRoles_sets_an_in_tenant_target_to_exactly_the_requested_roles()
    {
        var users = new FakeUsers();
        var target = Approved(TenantA);
        users.Seed(target);
        var roles = new FakeRoles();
        var member = roles.SeedRole("tenant_member", ProducerRoleStatus.Active);
        var finance = roles.SeedRole("finance", ProducerRoleStatus.Active);
        // pre-existing assignment to `member`; request only `finance` -> add finance, remove member.
        roles.Assignments.Add(ProducerRoleAssignment.Create(target.Id, member.Id, TenantA, Actor, Now));
        var handler = new SetProducerUserRolesHandler(users, roles, new FakeUow(), new FakeClock());

        await handler.Handle(new SetProducerUserRolesCommand(target.Id, ["finance"], TenantA, Actor), default);

        var roleIds = roles.Assignments.Where(a => a.TenantUserId == target.Id).Select(a => a.RoleId).ToHashSet();
        Assert.Equal(new HashSet<Guid> { finance.Id }, roleIds);
        Assert.All(roles.Assignments.Where(a => a.TenantUserId == target.Id), a => Assert.Equal(TenantA, a.TenantId));
        Assert.All(roles.Assignments.Where(a => a.TenantUserId == target.Id), a => Assert.Equal(Actor, a.AssignedByAdminId));
    }

    private static TenantUser Approved(Guid tenantId)
    {
        var u = TenantUser.Register(Guid.NewGuid().ToString(), "p@org.com", Now);
        u.Approve(tenantId, Now);
        return u;
    }

    private sealed class FakeUow : IProducerUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(1);
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct) => op(ct);
    }

    private sealed class FakeClock : IClock { public DateTime UtcNow => Now; }

    private sealed class FakeUsers : ITenantUserRepository
    {
        private readonly Dictionary<Guid, TenantUser> _byId = [];
        public void Seed(TenantUser u) => _byId[u.Id] = u;
        public Task<TenantUser?> FindByIdAsync(Guid id, CancellationToken ct) => Task.FromResult(_byId.GetValueOrDefault(id));
        public Task<TenantUser?> FindBySubjectAsync(string subject, CancellationToken ct) => throw new NotSupportedException();
        public void Add(TenantUser user) => throw new NotSupportedException();
    }

    private sealed class FakeRoles : IProducerRoleRepository
    {
        private readonly Dictionary<string, ProducerRole> _byCode = [];
        public readonly List<ProducerRoleAssignment> Assignments = [];

        public ProducerRole SeedRole(string code, ProducerRoleStatus status)
        {
            var role = ProducerRole.Create(code, code, null, null, status, [], ProducerPermissions.AllKeys);
            _byCode[code] = role;
            return role;
        }

        public void Add(ProducerRole role) => _byCode[role.Code] = role;
        public void Remove(ProducerRole role) => _byCode.Remove(role.Code);
        public void AddAssignment(ProducerRoleAssignment assignment) => Assignments.Add(assignment);
        public void RemoveAssignment(ProducerRoleAssignment assignment) => Assignments.Remove(assignment);

        public Task<ProducerRole?> GetByCodeAsync(string code, CancellationToken ct) => Task.FromResult(_byCode.GetValueOrDefault(code));
        public Task<bool> CodeExistsAsync(string code, CancellationToken ct) => Task.FromResult(_byCode.ContainsKey(code));
        public Task<int> CountAssignmentsForRoleAsync(Guid roleId, CancellationToken ct) =>
            Task.FromResult(Assignments.Count(a => a.RoleId == roleId));
        public Task<IReadOnlySet<string>> ListCatalogKeysAsync(CancellationToken ct) =>
            Task.FromResult(ProducerPermissions.AllKeys);
        public Task<IReadOnlyDictionary<string, Guid>> GetRoleIdsByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, Guid>>(
                _byCode.Where(kv => codes.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value.Id));
        public Task<IReadOnlySet<Guid>> ListRoleIdsForUserAsync(Guid tenantUserId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(Assignments.Where(a => a.TenantUserId == tenantUserId).Select(a => a.RoleId).ToHashSet());
        public Task<ProducerRoleAssignment?> GetAssignmentAsync(Guid tenantUserId, Guid roleId, CancellationToken ct) =>
            Task.FromResult(Assignments.FirstOrDefault(a => a.TenantUserId == tenantUserId && a.RoleId == roleId));

        // Unused by the handlers under test.
        public Task<IReadOnlyList<ProducerRoleListItem>> ListAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerRoleListItem?> GetListItemByCodeAsync(string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AssignmentExistsAsync(Guid tenantUserId, Guid roleId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProducerPermissionCatalogResult> ListCatalogAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> ListEffectivePermissionsAsync(Guid tenantUserId, Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListActiveRoleCodesForUserAsync(Guid tenantUserId, Guid tenantId, CancellationToken ct) => throw new NotSupportedException();
    }
}
