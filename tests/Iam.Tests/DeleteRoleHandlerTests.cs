using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Iam.Tests;

/// <summary>Delete-role guards (REQ-2.4/6.3), moved from <c>Admins.Tests.AdminRoleHandlerTests</c> onto the
/// unified <see cref="DeleteRoleHandler"/> (rf2). A seed anchor is the recovery/undeletable guarantee: blocked
/// even with zero bound users. A normal, unbound role deletes cleanly.</summary>
public sealed class DeleteRoleHandlerTests
{
    private static readonly RoleSideContext Platform = RoleSideContext.Platform();
    private static readonly IReadOnlyDictionary<string, Scope> NoCatalog = new Dictionary<string, Scope>();

    private static Role Seed(string code) =>
        Role.Create(code, code, null, null, RoleStatus.Active, Scope.Platform, null, [], NoCatalog);

    private static DeleteRoleHandler Handler(FakeRoleStore roles, FakeRoleAssignmentCounter? counter = null) =>
        new(roles, counter ?? new FakeRoleAssignmentCounter(), NullRoleAuditSink.Instance, new FakeUnitOfWork());

    [Fact]
    public async Task Deleting_the_platform_admin_seed_is_blocked_even_with_no_bound_users()
    {
        var roles = new FakeRoleStore();
        roles.Add(Seed(Role.PlatformAdminCode));

        await Assert.ThrowsAsync<ConflictException>(() =>
            Handler(roles).Handle(new DeleteRoleCommand(Platform, Role.PlatformAdminCode, "corr"), default).AsTask());

        Assert.Single(roles.Roles); // anchor not removed
    }

    [Fact]
    public async Task A_normal_role_with_no_bound_users_is_deleted()
    {
        var roles = new FakeRoleStore();
        roles.Add(Seed("ops_admin"));

        var result = await Handler(roles).Handle(new DeleteRoleCommand(Platform, "ops_admin", "corr"), default);

        Assert.Equal("ops_admin", result.Code);
        Assert.Empty(roles.Roles);
    }

    [Fact]
    public async Task A_role_with_bound_users_is_undeletable()
    {
        var role = Seed("ops_admin");
        var roles = new FakeRoleStore();
        roles.Add(role);
        var counter = new FakeRoleAssignmentCounter { Counts = { [role.Id] = 1 } };

        await Assert.ThrowsAsync<ConflictException>(() =>
            Handler(roles, counter).Handle(new DeleteRoleCommand(Platform, "ops_admin", "corr"), default).AsTask());

        Assert.Single(roles.Roles);
    }

    [Fact]
    public async Task A_merchant_deleting_a_role_owned_by_another_merchant_is_invisible_not_found()
    {
        // A role owned by a DIFFERENT specific merchant is outside RoleVisibility entirely -> 404, no leak.
        // The 409 ownership guard only fires for a SHARED (MerchantId: null) role a merchant can see but not own.
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var role = Role.Create("ops", "ops", null, null, RoleStatus.Active, Scope.Merchant, owner, [], NoCatalog);
        var roles = new FakeRoleStore();
        roles.Add(role);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Handler(roles).Handle(new DeleteRoleCommand(RoleSideContext.Merchant(other), "ops", "corr"), default).AsTask());

        Assert.Single(roles.Roles);
    }

    [Fact]
    public async Task A_merchant_deleting_a_visible_shared_seed_it_does_not_own_is_blocked()
    {
        var other = Guid.NewGuid();
        var shared = Role.Create("finance", "finance", null, null, RoleStatus.Active, Scope.Merchant, null, [], NoCatalog);
        var roles = new FakeRoleStore();
        roles.Add(shared);

        await Assert.ThrowsAsync<ConflictException>(() =>
            Handler(roles).Handle(new DeleteRoleCommand(RoleSideContext.Merchant(other), "finance", "corr"), default).AsTask());

        Assert.Single(roles.Roles);
    }

    internal sealed class FakeRoleStore : IRoleStore
    {
        public List<Role> Roles { get; } = [];

        public void Add(Role role) => Roles.Add(role);
        public void Remove(Role role) => Roles.Remove(role);

        public Task<Role?> GetByCodeAsync(RoleSideContext context, string code, CancellationToken ct) =>
            Task.FromResult(Roles.Where(RoleVisibility.For(context.Scope, context.MerchantId).Compile())
                .FirstOrDefault(r => r.Code == code));

        public Task<bool> CodeExistsAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<PagedResult<RoleListItem>> ListAsync(RoleSideContext context, PagedQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<RoleListItem?> GetListItemByCodeAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<Iam.Application.Permissions.PermissionCatalogResult> ListCatalogAsync(Scope scope, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class FakeRoleAssignmentCounter : IRoleAssignmentCounter
    {
        public Dictionary<Guid, int> Counts { get; } = [];
        public Task<int> CountAsync(RoleSideContext context, Guid roleId, CancellationToken ct) => Task.FromResult(Counts.GetValueOrDefault(roleId));
        public Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
            RoleSideContext context, IReadOnlyCollection<Guid> roleIds, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    internal sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            await operation(ct);
    }
}
