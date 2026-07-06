using System.Text.Json;
using Admin.Domain;
using Admin.Infrastructure.Persistence;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Admin.Tests;

/// <summary>
/// The admin-directory SFS apply pipeline (admin-account-management REQ-1). In-memory <c>List.AsQueryable</c>
/// cases prove whitelist gating, silent-drop, AND-combine, the strict tier/status parse, the coercion guard
/// (wrong-typed value -> ArgumentException -> 400, eagerly), and the multi-key sort where the account id closes
/// the chain WITHOUT killing an earlier key (REQ-1.3/F3). SQLite cases prove the LIKE-wildcard escaping that
/// needs a real relational provider (REQ-1.4).
/// </summary>
public sealed class AdminAccountSfsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AdminAccount Super(string email, DateTime createdAt) =>
        AdminAccount.SelfProvision(Guid.NewGuid().ToString("N"), email, createdAt);
    private static AdminAccount Scoped(string email, DateTime createdAt) =>
        AdminAccount.CreateScoped(email, createdAt);

    private static readonly DateTime T0 = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IQueryable<AdminAccount> Accounts(params AdminAccount[] accounts) => accounts.AsQueryable();

    // ===== filter whitelist gating =====
    [Fact]
    public void Unknown_field_is_silently_dropped()
    {
        var filtered = Accounts(Scoped("a@x", T0), Scoped("b@x", T0))
            .ApplyFilters([new FilterOption("subject", FilterOperator.Equals, J("\"x\""))]).ToList();
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void Wrong_case_field_is_silently_dropped_and_logged_without_value()
    {
        var log = new CapturingLogger();
        var filtered = Accounts(Scoped("a@x", T0))
            .ApplyFilters([new FilterOption("Email", FilterOperator.Equals, J("\"a@x\""))], log).ToList();

        Assert.Single(filtered);   // "Email" != "email" under Ordinal -> treated as absent (REQ-1.8)
        Assert.Contains(log.Messages, m => m.Contains("Email"));
        Assert.DoesNotContain(log.Messages, m => m.Contains("a@x"));   // value is NEVER logged
    }

    [Fact]
    public void Operator_not_allowed_on_tier_is_dropped()
    {
        // tier allows only eq/in; a Like on tier is dropped, not applied.
        var filtered = Accounts(Super("a@x", T0), Scoped("b@x", T0))
            .ApplyFilters([new FilterOption("tier", FilterOperator.Like, J("\"super\""))]).ToList();
        Assert.Equal(2, filtered.Count);
    }

    [Fact]
    public void Multiple_filters_AND_combine()
    {
        var filtered = Accounts(
                Super("keep@x", T0),
                Scoped("keep2@x", T0),
                Super("other@x", T0))
            .ApplyFilters([
                new FilterOption("tier", FilterOperator.Equals, J("\"super\"")),
                new FilterOption("email", FilterOperator.Equals, J("\"keep@x\"")),
            ]).ToList();

        Assert.Single(filtered);
        Assert.Equal("keep@x", filtered[0].Email);
    }

    [Theory]
    [InlineData("super", AdminTier.Super)]
    [InlineData("scoped", AdminTier.Scoped)]
    public void Tier_filter_parses_lowercase_wire_value(string wire, AdminTier expected)
    {
        var kept = Accounts(Super("s@x", T0), Scoped("c@x", T0))
            .ApplyFilters([new FilterOption("tier", FilterOperator.Equals, J($"\"{wire}\""))]).ToList();
        Assert.Single(kept);
        Assert.Equal(expected, kept[0].Tier);
    }

    [Fact]
    public void Status_filter_parses_lowercase_wire_value()
    {
        var suspended = Scoped("b@x", T0);
        suspended.Suspend(Guid.NewGuid());
        var kept = Accounts(Scoped("a@x", T0), suspended)
            .ApplyFilters([new FilterOption("status", FilterOperator.Equals, J("\"suspended\""))]).ToList();
        Assert.Single(kept);
        Assert.Equal("b@x", kept[0].Email);
    }

    // ===== coercion guard + strict parse -> 400 (REQ-1.7) =====
    [Fact]
    public void Wrong_typed_value_throws_ArgumentException_not_409_or_500()
    {
        // tier expects a lowercase string token; a JSON number must be a 400 (ArgumentException), raised eagerly.
        Assert.Throws<ArgumentException>(() =>
            Accounts(Scoped("a@x", T0)).ApplyFilters([new FilterOption("tier", FilterOperator.Equals, J("5"))]));
    }

    [Theory]
    [InlineData("tier", "\"admin\"")]
    [InlineData("status", "\"deleted\"")]
    public void Out_of_domain_enum_token_throws_ArgumentException(string field, string valueJson)
    {
        Assert.Throws<ArgumentException>(() =>
            Accounts(Scoped("a@x", T0)).ApplyFilters([new FilterOption(field, FilterOperator.Equals, J(valueJson))]));
    }

    // ===== sort: default + explicit + id closes the chain (REQ-1.3) =====
    [Fact]
    public void Default_sort_is_created_at_descending()
    {
        var sorted = Accounts(
                Scoped("a@x", T0), Scoped("c@x", T0.AddDays(2)), Scoped("b@x", T0.AddDays(1)))
            .ApplySort([]).Select(a => a.Email).ToList();
        Assert.Equal(new[] { "c@x", "b@x", "a@x" }, sorted);   // newest first (REQ-1.3 default)
    }

    [Fact]
    public void Explicit_email_sort_ascending()
    {
        var sorted = Accounts(Scoped("c@x", T0), Scoped("a@x", T0), Scoped("b@x", T0))
            .ApplySort([new SortOption("email")]).Select(a => a.Email).ToList();
        Assert.Equal(new[] { "a@x", "b@x", "c@x" }, sorted);
    }

    [Fact]
    public void Id_is_the_LAST_tiebreak_not_inserted_before_a_later_key()
    {
        // Two accounts share createdAt; a second sort key (email) MUST still decide their order — proving id
        // closes the chain once at the end, rather than being appended after every key (which would let the
        // unique id preempt email). F3.
        var sorted = Accounts(
                Scoped("b@x", T0), Scoped("a@x", T0), Scoped("z@x", T0.AddDays(1)))
            .ApplySort([new SortOption("createdAt"), new SortOption("email")])
            .Select(a => a.Email).ToList();
        // createdAt asc groups the two T0 rows first, email breaks their tie (a before b), then the T0+1 row.
        Assert.Equal(new[] { "a@x", "b@x", "z@x" }, sorted);
    }

    [Fact]
    public void Unknown_sort_field_falls_back_to_default()
    {
        var sorted = Accounts(Scoped("a@x", T0), Scoped("b@x", T0.AddDays(1)))
            .ApplySort([new SortOption("bogus")]).Select(a => a.Email).ToList();
        Assert.Equal(new[] { "b@x", "a@x" }, sorted);   // newest first
    }

    // ===== relational (SQLite): LIKE escape on email search =====
    [Fact]
    public async Task Search_escapes_percent_so_it_matches_literally()
    {
        using var db = NewDb(Scoped("50%@x", T0), Scoped("500@x", T0));
        var hits = await db.Accounts.ApplySearch(new SearchOption("50%", ["email"])).Select(a => a.Email).ToListAsync();
        Assert.Equal(new[] { "50%@x" }, hits);   // unescaped, "%" would also match "500@x"
    }

    [Fact]
    public async Task Filter_contains_escapes_wildcards()
    {
        using var db = NewDb(Scoped("a_b@x", T0), Scoped("axb@x", T0));
        var hits = await db.Accounts
            .ApplyFilters([new FilterOption("email", FilterOperator.Contains, J("\"a_b\""))])
            .Select(a => a.Email).ToListAsync();
        Assert.Equal(new[] { "a_b@x" }, hits);
    }

    // ---- SQLite standalone context (maps only AdminAccount) ----
    private static AccountDb NewDb(params AdminAccount[] seed)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var db = new AccountDb(connection);
        db.Database.EnsureCreated();
        db.Accounts.AddRange(seed);
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return db;
    }

    private sealed class AccountDb(SqliteConnection connection) : DbContext
    {
        public DbSet<AdminAccount> Accounts => Set<AdminAccount>();

        protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(connection);

        protected override void OnModelCreating(ModelBuilder model)
        {
            var e = model.Entity<AdminAccount>();
            e.ToTable("AdminAccounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Subject).HasMaxLength(256);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Tier).HasConversion<int>().IsRequired();
            e.Property(x => x.Status).HasConversion<int>().IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
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
