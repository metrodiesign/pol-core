using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Products.Application;
using Products.Domain;
using Products.Infrastructure;
using SharedKernel;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Products.Tests;

/// <summary>
/// The SFS-paged <c>ProductRepository.ListAsync</c> over a real <see cref="PolDbContext"/> backed by
/// in-memory SQLite (Products EF config applied via <see cref="ModuleAssemblies"/>). Proves: the merchant guard
/// <c>.Where(MerchantId)</c> confines every row to the bound merchant so SFS cannot surface another merchant's data
/// (REQ-7.1 — the app-layer belt; the SQL RLS floor is covered by Integration RlsIsolationTests), a paged slice
/// with total-after-filter (REQ-2.4/2.5), the typed <see cref="ProductFilterDto"/> narrowing (REQ-10), and the
/// scalar projection + escaped LIKE search over a real provider.
/// </summary>
public sealed class ProductRepositoryListTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly PolDbContext _seed;

    public ProductRepositoryListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _seed = NewContext();
        _seed.Database.EnsureCreated();
    }

    private PolDbContext NewContext() =>
        new(new DbContextOptionsBuilder<PolDbContext>().UseSqlite(_connection).Options,
            new ModuleAssemblies([typeof(ProductSfs).Assembly]));

    private ProductRepository Repo() => new(NewContext(), NullLogger<ProductRepository>.Instance);

    private void Seed(params Product[] products)
    {
        _seed.Set<Product>().AddRange(products);
        _seed.SaveChanges();
        _seed.ChangeTracker.Clear();
    }

    private static Product Prod(Guid merchant, string name, decimal price, bool active = true, int dayOffset = 0)
    {
        var p = Product.Create(merchant, name, Money.Of(price, "THB"), T0.AddDays(dayOffset));
        if (!active) p.Deactivate();
        return p;
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Lists_only_the_bound_tenants_products_even_with_a_filter()
    {
        Seed(Prod(TenantA, "a1", 100), Prod(TenantA, "a2", 200), Prod(TenantB, "b1", 150));

        var page = await Repo().ListAsync(new ListProductsQuery
        {
            MerchantId = TenantA,
            Filters = [new FilterOption("priceAmount", FilterOperator.GreaterThanOrEqual, J("0"))],
        }, CancellationToken.None);

        Assert.Equal(2, page.Total);                              // merchant B's row is never counted
        Assert.All(page.Items, i => Assert.Equal(TenantA, i.MerchantId));
    }

    [Fact]
    public async Task Returns_a_paged_slice_with_total()
    {
        Seed(
            Prod(TenantA, "p1", 100), Prod(TenantA, "p2", 100), Prod(TenantA, "p3", 100),
            Prod(TenantA, "p4", 100), Prod(TenantA, "p5", 100));

        var page = await Repo().ListAsync(
            new ListProductsQuery { MerchantId = TenantA, Page = 1, Limit = 2, Sort = [new SortOption("name")] },
            CancellationToken.None);

        Assert.Equal(5, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new[] { "p1", "p2" }, page.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Typed_product_filters_narrow_by_min_price_and_active()
    {
        Seed(
            Prod(TenantA, "cheap", 100),
            Prod(TenantA, "mid", 200),
            Prod(TenantA, "pricey", 300, active: false));

        var page = await Repo().ListAsync(new ListProductsQuery
        {
            MerchantId = TenantA,
            ProductFilters = new ProductFilterDto { MinPriceAmount = 150m, ActiveOnly = true },
            Sort = [new SortOption("name")],
        }, CancellationToken.None);

        Assert.Equal("mid", Assert.Single(page.Items).Name);   // >=150 AND active -> only "mid"
    }

    [Fact]
    public async Task Search_escapes_wildcards_and_projects_scalar_price()
    {
        Seed(Prod(TenantA, "50% off", 500), Prod(TenantA, "500 baht", 100));

        var page = await Repo().ListAsync(
            new ListProductsQuery { MerchantId = TenantA, Search = new SearchOption("50%", ["name"]) },
            CancellationToken.None);

        var item = Assert.Single(page.Items);        // "%" is literal, so "500 baht" does not match
        Assert.Equal("50% off", item.Name);
        Assert.Equal(500m, item.Price.Amount);
        Assert.Equal("THB", item.Price.Currency);
    }

    public void Dispose()
    {
        _seed.Dispose();
        _connection.Dispose();
    }
}
