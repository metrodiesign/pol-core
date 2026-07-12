using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Iam.Tests;

/// <summary>Update-role guards (REQ-2.4/3.8/6.3), moved from <c>Merchants.Tests.MerchantUserRoleHandlerTests</c>
/// onto the unified <see cref="UpdateRoleHandler"/> (rf2). Deactivating a seed anchor is a clean 409 before the
/// aggregate's own backstop throws; a Merchant-side caller mutating a role it can see but does not own
/// (a shared seed) is likewise 409.</summary>
public sealed class UpdateRoleHandlerTests
{
    private static readonly RoleSideContext Platform = RoleSideContext.Platform();
    private static readonly IReadOnlyDictionary<string, Scope> NoCatalog = new Dictionary<string, Scope>();

    private static UpdateRoleHandler Handler(FakeRoleStore roles) =>
        new(roles, new FakeRoleAssignmentCounter(), NullRoleAuditSink.Instance, new FakeUnitOfWork());

    private static Role Seed(string code, Scope scope = Scope.Platform, Guid? merchantId = null) =>
        Role.Create(code, code, null, null, RoleStatus.Active, scope, merchantId, [], NoCatalog);

    [Fact]
    public async Task Update_rejects_deactivating_the_platform_admin_anchor()
    {
        var anchor = Seed(Role.PlatformAdminCode);
        var roles = new FakeRoleStore { ByCode = anchor };

        await Assert.ThrowsAsync<ConflictException>(() => Handler(roles).Handle(
            new UpdateRoleCommand(Platform, Role.PlatformAdminCode, "Anchor", null, null, RoleStatus.Inactive, [], "corr"), default).AsTask());
    }

    [Fact]
    public async Task Update_allows_deactivating_an_ordinary_role()
    {
        var role = Seed("ops");
        var roles = new FakeRoleStore { ByCode = role };

        var result = await Handler(roles).Handle(
            new UpdateRoleCommand(Platform, "ops", "Ops", null, null, RoleStatus.Inactive, [], "corr"), default);

        Assert.Equal(RoleStatus.Inactive, result.Status);
    }

    [Fact]
    public async Task A_merchant_updating_a_role_owned_by_another_merchant_is_invisible_not_found()
    {
        // A role owned by a DIFFERENT specific merchant is outside RoleVisibility entirely -> 404, no leak.
        // The 409 ownership guard only fires for a SHARED (MerchantId: null) role a merchant can see but not own.
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var role = Seed("ops", Scope.Merchant, owner);
        var roles = new FakeRoleStore { ByCode = role };

        await Assert.ThrowsAsync<NotFoundException>(() => Handler(roles).Handle(
            new UpdateRoleCommand(RoleSideContext.Merchant(other), "ops", "Ops", null, null, RoleStatus.Active, [], "corr"), default).AsTask());
    }

    [Fact]
    public async Task A_merchant_updating_a_visible_shared_seed_it_does_not_own_is_blocked()
    {
        var other = Guid.NewGuid();
        var shared = Seed("finance", Scope.Merchant, null);
        var roles = new FakeRoleStore { ByCode = shared };

        await Assert.ThrowsAsync<ConflictException>(() => Handler(roles).Handle(
            new UpdateRoleCommand(RoleSideContext.Merchant(other), "finance", "Finance", null, null, RoleStatus.Active, [], "corr"), default).AsTask());
    }

    internal sealed class FakeRoleStore : IRoleStore
    {
        public Role? ByCode { get; set; }

        public void Add(Role role) => throw new NotSupportedException();
        public void Remove(Role role) => throw new NotSupportedException();
        // Mirrors the real RoleStore: GetByCodeAsync filters through RoleVisibility FIRST — a role owned by a
        // different specific merchant is invisible (null -> the handler's own 404), not merely "not owned".
        public Task<Role?> GetByCodeAsync(RoleSideContext context, string code, CancellationToken ct) =>
            Task.FromResult(ByCode is not null && RoleVisibility.For(context.Scope, context.MerchantId).Compile()(ByCode) ? ByCode : null);
        public Task<bool> CodeExistsAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<PagedResult<RoleListItem>> ListAsync(RoleSideContext context, PagedQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<RoleListItem?> GetListItemByCodeAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<Iam.Application.Permissions.PermissionCatalogResult> ListCatalogAsync(Scope scope, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class FakeRoleAssignmentCounter : IRoleAssignmentCounter
    {
        public Task<int> CountAsync(Guid roleId, CancellationToken ct) => Task.FromResult(0);
        public Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    internal sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            await operation(ct);
    }
}
