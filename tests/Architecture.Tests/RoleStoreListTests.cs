using System.Text.Json;
using BuildingBlocks.Application;
using Iam.Application.Roles;
using Iam.Domain.Permissions;
using Iam.Domain.Roles;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Iam;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Architecture.Tests;

/// <summary>
/// The SFS-paged <see cref="RoleStore.ListAsync"/> over <see cref="ControlPlaneDbContext"/> backed by
/// in-memory SQLite (ported from <c>Iam.Tests.RoleStoreListTests</c> onto the new runtime context, task
/// 8.5.8 — the class moved into this assembly and is internal, so only a project with InternalsVisibleTo can
/// construct it directly). Proves a paged slice with a total counted after filter/search but before paging
/// (REQ-2.4/2.5). <see cref="RoleListItem.UserCount"/> composition (RoleStore always returns 0 — the caller
/// fills it via <c>IRoleAssignmentCounter</c>) is covered separately in <c>ListRolesHandlerTests</c>.
/// </summary>
public sealed class RoleStoreListTests : IDisposable
{
    private static readonly IReadOnlyDictionary<string, Scope> NoCatalog = new Dictionary<string, Scope>();
    private readonly SqliteConnection _connection;

    public RoleStoreListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private RoleStore Store() => new(NewContext(), NullLogger<RoleStore>.Instance);

    private static Role MakeRole(string code, string name, string? description = null,
        RoleStatus status = RoleStatus.Active) =>
        Role.Create(code, name, description, null, status, Scope.Platform, null, [], NoCatalog);

    private void Seed(params Role[] roles)
    {
        using var seed = NewContext();
        seed.Set<Role>().AddRange(roles);
        seed.SaveChanges();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly RoleSideContext Platform = RoleSideContext.Platform();

    [Fact]
    public async Task Returns_a_paged_slice_with_total_across_all_matches()
    {
        Seed(MakeRole("r1", "One"), MakeRole("r2", "Two"), MakeRole("r3", "Three"), MakeRole("r4", "Four"), MakeRole("r5", "Five"));

        var page = await Store().ListAsync(Platform,
            new ListRolesQuery { Context = Platform, Page = 1, Limit = 2, Sort = [new SortOption("code")] }, CancellationToken.None);

        Assert.Equal(5, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new[] { "r1", "r2" }, page.Items.Select(i => i.Code));
    }

    [Fact]
    public async Task Second_page_returns_the_next_slice()
    {
        Seed(MakeRole("r1", "One"), MakeRole("r2", "Two"), MakeRole("r3", "Three"), MakeRole("r4", "Four"), MakeRole("r5", "Five"));

        var page = await Store().ListAsync(Platform,
            new ListRolesQuery { Context = Platform, Page = 2, Limit = 2, Sort = [new SortOption("code")] }, CancellationToken.None);

        Assert.Equal(new[] { "r3", "r4" }, page.Items.Select(i => i.Code));
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public async Task Total_counts_after_filter_before_paging()
    {
        Seed(
            MakeRole("a1", "A", status: RoleStatus.Active),
            MakeRole("a2", "A", status: RoleStatus.Active),
            MakeRole("a3", "A", status: RoleStatus.Active),
            MakeRole("i1", "I", status: RoleStatus.Inactive),
            MakeRole("i2", "I", status: RoleStatus.Inactive));

        var page = await Store().ListAsync(Platform, new ListRolesQuery
        {
            Context = Platform,
            Page = 1,
            Limit = 2,
            Filters = [new FilterOption("status", FilterOperator.Equals, J("\"active\""))],
            Sort = [new SortOption("code")],
        }, CancellationToken.None);

        Assert.Equal(3, page.Total);         // counted after filter, before paging (REQ-2.5)
        Assert.Equal(2, page.Items.Count);   // just this page
    }

    [Fact]
    public async Task Every_item_reports_zero_user_count_the_store_defers_to_the_counter()
    {
        Seed(MakeRole("lonely", "Lonely"));

        var page = await Store().ListAsync(Platform, new ListRolesQuery { Context = Platform }, CancellationToken.None);

        Assert.Equal(0, page.Items.Single().UserCount);
    }

    [Fact]
    public async Task Search_narrows_the_result_set()
    {
        Seed(MakeRole("finance_admin", "Finance"), MakeRole("support", "Support"));

        var page = await Store().ListAsync(Platform,
            new ListRolesQuery { Context = Platform, Search = new SearchOption("finance", ["name"]) }, CancellationToken.None);

        Assert.Equal(1, page.Total);
        Assert.Equal("finance_admin", page.Items.Single().Code);
    }

    [Fact]
    public async Task Merchant_scope_excludes_platform_rows()
    {
        var merchantId = Guid.NewGuid();
        Seed(MakeRole("plat", "Platform"));
        var merch = Role.Create("merch", "Merch", null, null, RoleStatus.Active, Scope.Merchant, merchantId, [], NoCatalog);
        Seed(merch);

        var page = await Store().ListAsync(
            RoleSideContext.Merchant(merchantId),
            new ListRolesQuery { Context = RoleSideContext.Merchant(merchantId) }, CancellationToken.None);

        Assert.Equal("merch", page.Items.Single().Code);
    }

    public void Dispose() => _connection.Dispose();
}
