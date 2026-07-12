using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Roles;

namespace Iam.Tests;

/// <summary>
/// <see cref="ListRolesHandler"/>/<see cref="GetRoleHandler"/> compose the real per-role user count from
/// <see cref="IRoleAssignmentCounter"/> onto the zero-filled items <see cref="IRoleStore"/> returns (rf2 — the
/// store cannot see the Admins/Merchants assignment entity types, so counting moved to the host-level bridge).
/// Moved from <c>AdminRoleRepositoryListTests.Preserves_user_count_per_role</c>, which tested this composition
/// one layer down (inside <c>RoleRepository.ListAsync</c> itself) before the count left the store entirely.
/// </summary>
public sealed class ListRolesHandlerTests
{
    private static readonly RoleSideContext Platform = RoleSideContext.Platform();

    private static RoleListItem Item(Guid id, string code) =>
        new(id, code, code, null, null, RoleStatus.Active, Shared: false, [], UserCount: 0);

    [Fact]
    public async Task List_fills_in_the_real_count_per_role_from_the_counter()
    {
        var busy = Item(Guid.NewGuid(), "busy");
        var lonely = Item(Guid.NewGuid(), "lonely");
        var store = new FakeRoleStore { Page = new PagedResult<RoleListItem>([busy, lonely], 1, 20, 2) };
        var counter = new FakeRoleAssignmentCounter { Counts = { [busy.Id] = 2 } }; // lonely absent -> 0

        var result = await new ListRolesHandler(store, counter)
            .Handle(new ListRolesQuery { Context = Platform }, CancellationToken.None);

        Assert.Equal(2, result.Items.Single(i => i.Code == "busy").UserCount);
        Assert.Equal(0, result.Items.Single(i => i.Code == "lonely").UserCount);
    }

    [Fact]
    public async Task List_skips_the_counter_call_entirely_when_the_page_is_empty()
    {
        var store = new FakeRoleStore { Page = new PagedResult<RoleListItem>([], 1, 20, 0) };
        var counter = new FakeRoleAssignmentCounter();

        await new ListRolesHandler(store, counter).Handle(new ListRolesQuery { Context = Platform }, CancellationToken.None);

        Assert.False(counter.CountManyCalled);
    }

    [Fact]
    public async Task Get_fills_in_the_real_count_for_the_single_role()
    {
        var role = Item(Guid.NewGuid(), "busy");
        var store = new FakeRoleStore { ByCode = role };
        var counter = new FakeRoleAssignmentCounter { Counts = { [role.Id] = 5 } };

        var result = await new GetRoleHandler(store, counter).Handle(new GetRoleQuery(Platform, "busy"), CancellationToken.None);

        Assert.Equal(5, result!.UserCount);
    }

    [Fact]
    public async Task Get_returns_null_without_calling_the_counter_when_the_role_is_not_found()
    {
        var store = new FakeRoleStore { ByCode = null };
        var counter = new FakeRoleAssignmentCounter();

        var result = await new GetRoleHandler(store, counter).Handle(new GetRoleQuery(Platform, "missing"), CancellationToken.None);

        Assert.Null(result);
        Assert.False(counter.CountCalled);
    }

    private sealed class FakeRoleStore : IRoleStore
    {
        public PagedResult<RoleListItem> Page { get; set; } = new([], 1, 20, 0);
        public RoleListItem? ByCode { get; set; }

        public void Add(Role role) => throw new NotSupportedException();
        public void Remove(Role role) => throw new NotSupportedException();
        public Task<Role?> GetByCodeAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CodeExistsAsync(RoleSideContext context, string code, CancellationToken ct) => throw new NotSupportedException();
        public Task<PagedResult<RoleListItem>> ListAsync(RoleSideContext context, PagedQuery query, CancellationToken ct) =>
            Task.FromResult(Page);
        public Task<RoleListItem?> GetListItemByCodeAsync(RoleSideContext context, string code, CancellationToken ct) =>
            Task.FromResult(ByCode);
        public Task<Iam.Application.Permissions.PermissionCatalogResult> ListCatalogAsync(
            Iam.Domain.Permissions.Scope scope, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeRoleAssignmentCounter : IRoleAssignmentCounter
    {
        public Dictionary<Guid, int> Counts { get; } = [];
        public bool CountCalled { get; private set; }
        public bool CountManyCalled { get; private set; }

        public Task<int> CountAsync(RoleSideContext context, Guid roleId, CancellationToken ct)
        {
            CountCalled = true;
            return Task.FromResult(Counts.GetValueOrDefault(roleId));
        }

        public Task<IReadOnlyDictionary<Guid, int>> CountManyAsync(
            RoleSideContext context, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
        {
            CountManyCalled = true;
            IReadOnlyDictionary<Guid, int> result = Counts.Where(kv => roleIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return Task.FromResult(result);
        }
    }
}
