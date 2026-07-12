using System.Text.Json;
using Admins.Application;
using Admins.Application.MasterData;
using Admins.Application.Permissions;
using Admins.Application.Roles;
using Admins.Application.Users;
using Admins.Domain.MasterData;
using Admins.Domain.Permissions;
using Admins.Domain.Roles;
using Admins.Domain.Users;
using Admins.Infrastructure.Persistence;
using Admins.Infrastructure.Persistence.MasterData;
using Admins.Infrastructure.Persistence.Permissions;
using Admins.Infrastructure.Persistence.Roles;
using Admins.Infrastructure.Persistence.Users;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Admins.Tests;

/// <summary>
/// The Role SFS apply pipeline (control-plane exemplar). In-memory <c>List.AsQueryable</c> cases prove the
/// whitelist gating, silent-drop, AND-combine, status parsing, and the coercion guard (wrong-typed value ->
/// ArgumentException -> 400, eagerly). SQLite cases prove the two behaviours that need a real relational
/// provider: NULLS-last ordering on the nullable Description in both directions, and LIKE-wildcard escaping so a
/// term containing <c>%</c>/<c>_</c> matches literally. (REQ-3, REQ-4, REQ-5, REQ-6, REQ-8.5, REQ-8.6)
/// </summary>
public sealed class AdminRoleSfsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Role MakeRole(string code, string name, string? description = null,
        RoleStatus status = RoleStatus.Active) =>
        Role.Create(code, name, description, color: null, status, permissionKeys: [], catalogKeys: new HashSet<string>());

    private static IQueryable<Role> Roles(params Role[] roles) => roles.AsQueryable();

    // ===== EscapeLike (REQ-5.4, REQ-6.4) =====
    [Theory]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("x[y", "x\\[y")]
    [InlineData("c:\\d", "c:\\\\d")]
    [InlineData("plain", "plain")]
    public void EscapeLike_prefixes_each_wildcard(string input, string expected)
    {
        Assert.Equal(expected, SfsLike.Escape(input));
    }

    // ===== filter whitelist gating =====
    [Fact]
    public void Unknown_field_is_silently_dropped()
    {
        var filtered = Roles(MakeRole("a", "A"), MakeRole("b", "B"))
            .ApplyFilters([new FilterOption("secret", FilterOperator.Equals, J("\"x\""))]).ToList();
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void Wrong_case_field_is_silently_dropped()
    {
        var filtered = Roles(MakeRole("a", "A"))
            .ApplyFilters([new FilterOption("Code", FilterOperator.Equals, J("\"a\""))]).ToList();
        Assert.Single(filtered);   // "Code" != "code" under Ordinal -> treated as absent (REQ-6.7)
    }

    [Fact]
    public void Operator_not_allowed_on_field_is_dropped_and_logged_without_value()
    {
        var log = new CapturingLogger();
        var filtered = Roles(MakeRole("a", "A"))
            .ApplyFilters([new FilterOption("code", FilterOperator.GreaterThan, J("\"sekret\""))], log).ToList();

        Assert.Single(filtered);                                         // gt is not allowed on code
        Assert.Contains(log.Messages, m => m.Contains("code"));          // field name logged (REQ-8.6)
        Assert.DoesNotContain(log.Messages, m => m.Contains("sekret"));  // value is NEVER logged
    }

    [Fact]
    public void Empty_in_values_is_silently_dropped()
    {
        var filtered = Roles(MakeRole("a", "A"), MakeRole("b", "B"))
            .ApplyFilters([new FilterOption("code", FilterOperator.In, Values: [])]).ToList();
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void Multiple_filters_AND_combine()
    {
        var filtered = Roles(
                MakeRole("keep", "A", status: RoleStatus.Active),
                MakeRole("keep2", "A", status: RoleStatus.Inactive),
                MakeRole("other", "A", status: RoleStatus.Active))
            .ApplyFilters([
                new FilterOption("name", FilterOperator.Equals, J("\"A\"")),
                new FilterOption("status", FilterOperator.Equals, J("\"active\"")),
                new FilterOption("code", FilterOperator.Equals, J("\"keep\"")),
            ]).ToList();

        Assert.Single(filtered);
        Assert.Equal("keep", filtered[0].Code);
    }

    [Fact]
    public void Status_filter_parses_lowercase_wire_value()
    {
        var filtered = Roles(
                MakeRole("a", "A", status: RoleStatus.Active),
                MakeRole("b", "B", status: RoleStatus.Inactive))
            .ApplyFilters([new FilterOption("status", FilterOperator.Equals, J("\"inactive\""))]).ToList();

        Assert.Single(filtered);
        Assert.Equal("b", filtered[0].Code);
    }

    // ===== coercion guard -> 400 (REQ-8.5) =====
    [Fact]
    public void Wrong_typed_value_throws_ArgumentException_not_409_or_500()
    {
        // status expects a lowercase string token; a JSON number must be a 400 (ArgumentException), raised
        // eagerly during apply — never a 409 (InvalidOperationException) or 500 (FormatException).
        Assert.Throws<ArgumentException>(() =>
            Roles(MakeRole("a", "A")).ApplyFilters([new FilterOption("status", FilterOperator.Equals, J("5"))]));
    }

    [Fact]
    public void Invalid_status_token_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            Roles(MakeRole("a", "A")).ApplyFilters([new FilterOption("status", FilterOperator.Equals, J("\"deleted\""))]));
    }

    // ===== sort default fallback =====
    [Fact]
    public void Unknown_sort_field_falls_back_to_default_code_desc()
    {
        var sorted = Roles(MakeRole("a", "A"), MakeRole("c", "C"), MakeRole("b", "B"))
            .ApplySort([new SortOption("bogus")]).Select(r => r.Code).ToList();
        Assert.Equal(new[] { "c", "b", "a" }, sorted);   // mandatory default OrderByDescending(Code) (REQ-4.5)
    }

    // ===== relational (SQLite): NULLS-last + LIKE escape =====
    [Theory]
    [InlineData(SortDirection.Asc)]
    [InlineData(SortDirection.Desc)]
    public async Task Sort_by_nullable_description_places_nulls_last_in_both_directions(SortDirection dir)
    {
        using var db = NewDb(
            MakeRole("r_beta", "beta", "beta desc"),
            MakeRole("r_null", "null", null),
            MakeRole("r_alpha", "alpha", "alpha desc"));

        var codes = await db.Roles.ApplySort([new SortOption("description", dir)]).Select(r => r.Code).ToListAsync();

        Assert.Equal("r_null", codes[^1]);   // NULL description is last regardless of direction (REQ-4.4)
    }

    [Fact]
    public async Task Search_escapes_percent_so_it_matches_literally()
    {
        using var db = NewDb(MakeRole("r_pct", "50% off", null), MakeRole("r_num", "500 baht", null));

        var hits = await db.Roles.ApplySearch(new SearchOption("50%", ["name"])).Select(r => r.Code).ToListAsync();

        Assert.Equal(new[] { "r_pct" }, hits);   // unescaped, "%" would also match "500 baht"
    }

    [Fact]
    public async Task Search_escapes_underscore_so_it_matches_literally()
    {
        using var db = NewDb(MakeRole("r_us", "a_b", null), MakeRole("r_any", "axb", null));

        var hits = await db.Roles.ApplySearch(new SearchOption("a_b", ["name"])).Select(r => r.Code).ToListAsync();

        Assert.Equal(new[] { "r_us" }, hits);   // unescaped, "_" would also match "axb"
    }

    [Fact]
    public async Task Filter_contains_escapes_wildcards()
    {
        using var db = NewDb(MakeRole("r_pct", "50% off", null), MakeRole("r_num", "500 baht", null));

        var hits = await db.Roles
            .ApplyFilters([new FilterOption("name", FilterOperator.Contains, J("\"50%\""))])
            .Select(r => r.Code).ToListAsync();

        Assert.Equal(new[] { "r_pct" }, hits);
    }

    // ---- SQLite standalone context (maps only Role; ponytail: [ wildcard is SQL-Server-only, covered by
    // the EscapeLike output assertion above — SQLite does not treat [ specially) ----
    private static RoleDb NewDb(params Role[] seed)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var db = new RoleDb(connection);
        db.Database.EnsureCreated();
        db.Roles.AddRange(seed);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return db;
    }

    private sealed class RoleDb(SqliteConnection connection) : DbContext
    {
        public DbSet<Role> Roles => Set<Role>();

        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(connection);

        protected override void OnModelCreating(ModelBuilder model)
        {
            var e = model.Entity<Role>();
            e.ToTable("AdminRoles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.Description).HasMaxLength(256);
            e.Property(x => x.Color).HasMaxLength(16);
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Ignore(x => x.Permissions);
            e.Ignore(x => x.PermissionKeys);
            e.Ignore(x => x.IsSuperAdminSeed);
            e.Ignore(x => x.DomainEvents);
        }

        public override void Dispose()
        {
            base.Dispose();
            connection.Dispose();
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
