using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using Products.Domain;
using SharedKernel;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Architecture.Tests;

/// <summary>
/// The SFS-paged <see cref="ProductRepository.ListAsync"/> over <see cref="MerchantRuntimeDbContext"/> backed
/// by in-memory SQLite (ported from <c>Products.Tests.ProductRepositoryListTests</c> onto the new runtime
/// context, task 8.5.8 — the class moved into this assembly and is internal, so only a project with
/// InternalsVisibleTo can construct it directly). Proves: the merchant guard confines every row to the bound
/// merchant so SFS cannot surface another merchant's data (REQ-7.1 — the app-layer belt; the read floor
/// itself is covered by ReadFloorTests), a paged slice with total-after-filter (REQ-2.4/2.5), the typed
/// <see cref="ProductFilterDto"/> narrowing (REQ-10), and the scalar projection + escaped LIKE search over a
/// real provider.
/// </summary>
public sealed class ProductRepositoryListTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public ProductRepositoryListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    // ListAsync's SFS query is not merchant-guarded by the repository itself (the caller passes MerchantId
    // explicitly in the query, same as before the read-floor's query filter existed) — bind an arbitrary
    // actor so the ambient read floor does not also filter these rows out from under the explicit query.
    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private ProductRepository Repo() => new(NewContext(), NullLogger<ProductRepository>.Instance);

    private void Seed(params Product[] products)
    {
        using var seed = NewContext();
        seed.Set<Product>().AddRange(products);
        seed.SaveChanges();
    }

    private static Product Prod(Guid merchant, string name, decimal price, bool active = true, int dayOffset = 0)
    {
        var p = Product.Create(
            merchant, name, Money.Of(price, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            T0.AddDays(dayOffset));
        if (!active) p.Deactivate();
        return p;
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task Lists_only_the_bound_tenants_products_even_with_a_filter()
    {
        Seed(Prod(MerchantA, "a1", 100), Prod(MerchantA, "a2", 200), Prod(MerchantB, "b1", 150));

        var page = await Repo().ListAsync(new ListProductsQuery
        {
            MerchantId = MerchantA,
            Filters = [new FilterOption("priceAmount", FilterOperator.GreaterThanOrEqual, J("0"))],
        }, CancellationToken.None);

        Assert.Equal(2, page.Total);                              // merchant B's row is never counted
        Assert.All(page.Items, i => Assert.Equal(MerchantA, i.MerchantId));
    }

    [Fact]
    public async Task Returns_a_paged_slice_with_total()
    {
        Seed(
            Prod(MerchantA, "p1", 100), Prod(MerchantA, "p2", 100), Prod(MerchantA, "p3", 100),
            Prod(MerchantA, "p4", 100), Prod(MerchantA, "p5", 100));

        var page = await Repo().ListAsync(
            new ListProductsQuery { MerchantId = MerchantA, Page = 1, Limit = 2, Sort = [new SortOption("name")] },
            CancellationToken.None);

        Assert.Equal(5, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new[] { "p1", "p2" }, page.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Typed_product_filters_narrow_by_min_price_and_active()
    {
        Seed(
            Prod(MerchantA, "cheap", 100),
            Prod(MerchantA, "mid", 200),
            Prod(MerchantA, "pricey", 300, active: false));

        var page = await Repo().ListAsync(new ListProductsQuery
        {
            MerchantId = MerchantA,
            ProductFilters = new ProductFilterDto { MinPriceAmount = 150m, ActiveOnly = true },
            Sort = [new SortOption("name")],
        }, CancellationToken.None);

        Assert.Equal("mid", Assert.Single(page.Items).Name);   // >=150 AND active -> only "mid"
    }

    [Fact]
    public async Task Search_escapes_wildcards_and_projects_scalar_price()
    {
        Seed(Prod(MerchantA, "50% off", 500), Prod(MerchantA, "500 baht", 100));

        var page = await Repo().ListAsync(
            new ListProductsQuery { MerchantId = MerchantA, Search = new SearchOption("50%", ["name"]) },
            CancellationToken.None);

        var item = Assert.Single(page.Items);        // "%" is literal, so "500 baht" does not match
        Assert.Equal("50% off", item.Name);
        Assert.Equal(500m, item.Price.Amount);
        Assert.Equal("THB", item.Price.Currency);
    }

    public void Dispose() => _connection.Dispose();
}
