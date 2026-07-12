using System.Text.Json;
using Admins.Application.RoleQueries;
using Admins.Domain;
using Admins.Infrastructure.Persistence;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Admins.Tests;

/// <summary>
/// The SFS-paged <c>AdminRoleRepository.ListAsync</c> over a real <see cref="PolDbContext"/> backed by
/// in-memory SQLite (the Admin module's EF configs are applied via <see cref="ModuleAssemblies"/>). Proves the
/// wiring behaviours task 4 adds: a paged slice with a total counted after filter/search but before paging
/// (REQ-2.4, REQ-2.5), and <c>UserCount</c> preserved through the materialize-then-map path (REQ-12.1).
/// </summary>
public sealed class AdminRoleRepositoryListTests : IDisposable
{
    private static readonly IReadOnlySet<string> NoCatalog = new HashSet<string>();
    private readonly SqliteConnection _connection;
    private readonly PolDbContext _seed;

    public AdminRoleRepositoryListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _seed = NewContext();
        _seed.Database.EnsureCreated();
    }

    private PolDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_connection).Options,
            new ModuleAssemblies([typeof(AdminRoleSfs).Assembly]));

    private AdminRoleRepository Repo() => new(NewContext(), NullLogger<AdminRoleRepository>.Instance);

    private static AdminRole Role(string code, string name, string? description = null,
        AdminRoleStatus status = AdminRoleStatus.Active) =>
        AdminRole.Create(code, name, description, null, status, [], NoCatalog);

    private void Seed(params AdminRole[] roles)
    {
        _seed.Set<AdminRole>().AddRange(roles);
        _seed.SaveChanges();
        _seed.ChangeTracker.Clear();
    }

    private void Assign(Guid roleId, int count)
    {
        var when = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
            _seed.Set<AdminRoleAssignment>().Add(AdminRoleAssignment.Create(Guid.NewGuid(), roleId, Guid.NewGuid(), when));
        _seed.SaveChanges();
        _seed.ChangeTracker.Clear();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Returns_a_paged_slice_with_total_across_all_matches()
    {
        Seed(Role("r1", "One"), Role("r2", "Two"), Role("r3", "Three"), Role("r4", "Four"), Role("r5", "Five"));

        var page = await Repo().ListAsync(
            new ListRolesQuery { Page = 1, Limit = 2, Sort = [new SortOption("code")] }, CancellationToken.None);

        Assert.Equal(5, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new[] { "r1", "r2" }, page.Items.Select(i => i.Code));
    }

    [Fact]
    public async Task Second_page_returns_the_next_slice()
    {
        Seed(Role("r1", "One"), Role("r2", "Two"), Role("r3", "Three"), Role("r4", "Four"), Role("r5", "Five"));

        var page = await Repo().ListAsync(
            new ListRolesQuery { Page = 2, Limit = 2, Sort = [new SortOption("code")] }, CancellationToken.None);

        Assert.Equal(new[] { "r3", "r4" }, page.Items.Select(i => i.Code));
        Assert.Equal(5, page.Total);
    }

    [Fact]
    public async Task Total_counts_after_filter_before_paging()
    {
        Seed(
            Role("a1", "A", status: AdminRoleStatus.Active),
            Role("a2", "A", status: AdminRoleStatus.Active),
            Role("a3", "A", status: AdminRoleStatus.Active),
            Role("i1", "I", status: AdminRoleStatus.Inactive),
            Role("i2", "I", status: AdminRoleStatus.Inactive));

        var page = await Repo().ListAsync(new ListRolesQuery
        {
            Page = 1,
            Limit = 2,
            Filters = [new FilterOption("status", FilterOperator.Equals, J("\"active\""))],
            Sort = [new SortOption("code")],
        }, CancellationToken.None);

        Assert.Equal(3, page.Total);         // counted after filter, before paging (REQ-2.5)
        Assert.Equal(2, page.Items.Count);   // just this page
    }

    [Fact]
    public async Task Preserves_user_count_per_role()
    {
        var busy = Role("busy", "Busy");
        Seed(busy, Role("lonely", "Lonely"));
        Assign(busy.Id, 2);

        var page = await Repo().ListAsync(new ListRolesQuery { Sort = [new SortOption("code")] }, CancellationToken.None);

        Assert.Equal(2, page.Items.Single(i => i.Code == "busy").UserCount);
        Assert.Equal(0, page.Items.Single(i => i.Code == "lonely").UserCount);
    }

    [Fact]
    public async Task Search_narrows_the_result_set()
    {
        Seed(Role("finance_admin", "Finance"), Role("support", "Support"));

        var page = await Repo().ListAsync(
            new ListRolesQuery { Search = new SearchOption("finance", ["name"]) }, CancellationToken.None);

        Assert.Equal(1, page.Total);
        Assert.Equal("finance_admin", page.Items.Single().Code);
    }

    public void Dispose()
    {
        _seed.Dispose();
        _connection.Dispose();
    }
}
