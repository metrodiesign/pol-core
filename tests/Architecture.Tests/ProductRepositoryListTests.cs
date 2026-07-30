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
/// by in-memory SQLite (the class is internal, so only a project with InternalsVisibleTo can construct it
/// directly). Proves: the merchant guard confines every row to the bound merchant so SFS cannot surface
/// another merchant's data (REQ-7.1 — the app-layer belt; the read floor itself is covered by
/// ReadFloorTests), a paged slice with total-after-filter (REQ-2.4/2.5), the typed
/// <see cref="ProductFilterDto"/> narrowing (SP guide §2), and the scalar projection + escaped LIKE search
/// over a real provider.
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

    private static Product Prod(
        Guid merchant, string documentNo, decimal totalPremium, bool active = true, int dayOffset = 0,
        ProductGroup group = ProductGroup.VMI, DocumentType type = DocumentType.POLICY, string? showName = null)
    {
        var p = Product.Create(
            new ProductInput(
                merchant, group, type, documentNo, "100", "00098", Money.Of(totalPremium, "THB"),
                ShowName: showName),
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
            Filters = [new FilterOption("totalPremiumAmount", FilterOperator.GreaterThanOrEqual, J("0"))],
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
            new ListProductsQuery { MerchantId = MerchantA, Page = 1, Limit = 2, Sort = [new SortOption("documentNo")] },
            CancellationToken.None);

        Assert.Equal(5, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new[] { "p1", "p2" }, page.Items.Select(i => i.DocumentNo));
    }

    [Fact]
    public async Task Typed_product_filters_narrow_by_group_and_payment_status()
    {
        var paid = Prod(MerchantA, "paid-doc", 200);
        paid.MarkPaid(T0.AddDays(1));
        Seed(
            Prod(MerchantA, "unpaid-vmi", 100),
            Prod(MerchantA, "unpaid-fire", 200, group: ProductGroup.FIRE),
            paid);

        var page = await Repo().ListAsync(new ListProductsQuery
        {
            MerchantId = MerchantA,
            ProductFilters = new ProductFilterDto
            {
                ProductGroup = ProductGroup.VMI,
                PaymentStatus = PaymentStatus.UNPAID,
            },
            Sort = [new SortOption("documentNo")],
        }, CancellationToken.None);

        Assert.Equal("unpaid-vmi", Assert.Single(page.Items).DocumentNo);
    }

    [Fact]
    public async Task Typed_insured_name_filter_matches_partially()
    {
        Seed(
            Prod(MerchantA, "d1", 100, showName: "สมชาย ใจดี"),
            Prod(MerchantA, "d2", 100, showName: "สมหญิง รักดี"),
            Prod(MerchantA, "d3", 100));   // ShowName null -> never matches

        var page = await Repo().ListAsync(new ListProductsQuery
        {
            MerchantId = MerchantA,
            ProductFilters = new ProductFilterDto { InsuredName = "สมชาย" },
        }, CancellationToken.None);

        Assert.Equal("d1", Assert.Single(page.Items).DocumentNo);
    }

    [Fact]
    public async Task Search_escapes_wildcards_and_projects_scalar_premium()
    {
        Seed(Prod(MerchantA, "50% off", 500), Prod(MerchantA, "500 baht", 100));

        var page = await Repo().ListAsync(
            new ListProductsQuery { MerchantId = MerchantA, Search = new SearchOption("50%", ["documentNo"]) },
            CancellationToken.None);

        var item = Assert.Single(page.Items);        // "%" is literal, so "500 baht" does not match
        Assert.Equal("50% off", item.DocumentNo);
        Assert.Equal(500m, item.TotalPremium.Amount);
        Assert.Equal("THB", item.TotalPremium.Currency);
    }

    public void Dispose() => _connection.Dispose();
}
