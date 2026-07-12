using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;

namespace Iam.Tests;

/// <summary>Create-role guards (REQ-2.3/6.3), moved from <c>Merchants.Tests.MerchantUserRoleHandlerTests</c>
/// onto the unified <see cref="CreateRoleHandler"/> (rf2). A duplicate code within the context's visible set is
/// a 409 before the aggregate ever runs; an unknown permission key is the aggregate's own 400
/// (already covered at the domain level by <c>RoleTests</c>).</summary>
public sealed class CreateRoleHandlerTests
{
    private static readonly RoleSideContext Platform = RoleSideContext.Platform();
    private static readonly IReadOnlyDictionary<string, Scope> Catalog = Keys.KeySide;

    private static CreateRoleHandler Handler(FakeRoleStore roles) =>
        new(roles, NullRoleAuditSink.Instance, new FakeUnitOfWork());

    [Fact]
    public async Task Create_rejects_a_duplicate_code()
    {
        var roles = new FakeRoleStore();
        roles.ExistingCodes.Add("ops_admin");

        await Assert.ThrowsAsync<ConflictException>(() => Handler(roles).Handle(
            new CreateRoleCommand(Platform, "ops_admin", "dup", null, null, RoleStatus.Active, ["txn.view"], "corr"), default).AsTask());

        Assert.Empty(roles.Added);
    }

    [Fact]
    public async Task Create_persists_a_new_role_when_the_code_is_free()
    {
        var roles = new FakeRoleStore();

        var result = await Handler(roles).Handle(
            new CreateRoleCommand(Platform, "ops", "Ops", null, null, RoleStatus.Active, ["txn.view"], "corr"), default);

        Assert.Equal("ops", result.Code);
        Assert.Single(roles.Added);
    }

    internal sealed class FakeRoleStore : IRoleStore
    {
        public HashSet<string> ExistingCodes { get; } = [];
        public List<Role> Added { get; } = [];

        public void Add(Role role) => Added.Add(role);
        public void Remove(Role role) => throw new NotSupportedException();
        public Task<Role?> GetByCodeAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CodeExistsAsync(RoleSideContext context, string code, CancellationToken ct) => Task.FromResult(ExistingCodes.Contains(code));
        public Task<PagedResult<RoleListItem>> ListAsync(RoleSideContext context, PagedQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task<RoleListItem?> GetListItemByCodeAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<Iam.Application.Permissions.PermissionCatalogResult> ListCatalogAsync(Scope scope, CancellationToken ct) => throw new NotSupportedException();
    }

    internal sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct) => Task.FromResult(0);
        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
            await operation(ct);
    }
}
