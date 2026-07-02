using System.Text.Json;
using BuildingBlocks.Application;
using Products.Application;
using Products.Domain;
using Products.Infrastructure;
using SharedKernel;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Products.Tests;

/// <summary>
/// The tenant-scoped Product SFS pipeline (in-memory <c>List.AsQueryable</c> cases). Proves the range/numeric
/// operators (gt/gte/lt/lte/between on price + createdAt) and eq on the bool, the whitelist gating (including
/// that <c>tenantId</c> is never filterable — REQ-7.3), the coercion guard (wrong-typed value ->
/// ArgumentException -> 400), the mandatory default sort, and the typed <see cref="ProductFilterDto"/> parse
/// (REQ-10, REQ-8.3). LIKE-escape + tenant narrowing over a real provider live in ProductRepositoryListTests.
/// </summary>
public sealed class ProductSfsTests
{
    private static readonly Guid Tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Product P(string name, long price, bool active = true, int dayOffset = 0)
    {
        var p = Product.Create(Tenant, name, Money.Of(price, "THB"), T0.AddDays(dayOffset));
        if (!active) p.Deactivate();
        return p;
    }

    private static IQueryable<Product> Q(params Product[] products) => products.AsQueryable();

    // ---- whitelist gating ----
    [Fact]
    public void Unknown_field_is_dropped()
    {
        var r = Q(P("a", 100), P("b", 200))
            .ApplyFilters([new FilterOption("secret", FilterOperator.Equals, J("1"))]);
        Assert.Equal(2, r.Count());
    }

    [Fact]
    public void Tenant_id_is_never_filterable()
    {
        // tenantId is in no whitelist, so a filter naming it is dropped — SFS cannot widen tenant scope (REQ-7.3).
        var r = Q(P("a", 100)).ApplyFilters([new FilterOption("tenantId", FilterOperator.Equals, J("\"x\""))]);
        Assert.Single(r);
    }

    [Fact]
    public void Disallowed_operator_is_dropped()
    {
        var r = Q(P("a", 100, active: true), P("b", 200, active: false))
            .ApplyFilters([new FilterOption("isActive", FilterOperator.GreaterThan, J("true"))]);
        Assert.Equal(2, r.Count());   // isActive allows only eq
    }

    // ---- range / numeric operators ----
    [Theory]
    [InlineData(FilterOperator.GreaterThanOrEqual, 2)]
    [InlineData(FilterOperator.GreaterThan, 1)]
    [InlineData(FilterOperator.LessThanOrEqual, 2)]
    [InlineData(FilterOperator.LessThan, 1)]
    public void Price_comparison_operators(FilterOperator op, int expected)
    {
        var r = Q(P("a", 100), P("b", 200), P("c", 300))
            .ApplyFilters([new FilterOption("priceMinorUnits", op, J("200"))]);
        Assert.Equal(expected, r.Count());
    }

    [Fact]
    public void Price_between_filter()
    {
        var r = Q(P("a", 100), P("b", 200), P("c", 300))
            .ApplyFilters([new FilterOption("priceMinorUnits", FilterOperator.Between, Values: [J("150"), J("250")])]);
        Assert.Equal("b", Assert.Single(r).Name);
    }

    [Fact]
    public void Empty_between_values_is_dropped()
    {
        var r = Q(P("a", 100), P("b", 200))
            .ApplyFilters([new FilterOption("priceMinorUnits", FilterOperator.Between, Values: [J("150")])]);
        Assert.Equal(2, r.Count());   // fewer than 2 values -> silent-drop (REQ-3.6)
    }

    [Fact]
    public void IsActive_eq_filter()
    {
        var r = Q(P("a", 100, active: true), P("b", 200, active: false))
            .ApplyFilters([new FilterOption("isActive", FilterOperator.Equals, J("true"))]);
        Assert.Equal("a", Assert.Single(r).Name);
    }

    // ---- coercion guard -> 400 (REQ-8.5) ----
    [Fact]
    public void Wrong_typed_price_throws_ArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            Q(P("a", 100)).ApplyFilters([new FilterOption("priceMinorUnits", FilterOperator.GreaterThanOrEqual, J("\"abc\""))]));

    [Fact]
    public void Wrong_typed_isActive_throws_ArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            Q(P("a", 100)).ApplyFilters([new FilterOption("isActive", FilterOperator.Equals, J("\"nope\""))]));

    [Fact]
    public void Wrong_typed_createdAt_throws_ArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            Q(P("a", 100)).ApplyFilters([new FilterOption("createdAt", FilterOperator.GreaterThanOrEqual, J("123"))]));

    // ---- sort ----
    [Fact]
    public void Default_sort_is_created_at_desc()
    {
        var r = Q(P("a", 1, dayOffset: 0), P("b", 1, dayOffset: 2), P("c", 1, dayOffset: 1))
            .ApplySort([]).Select(p => p.Name).ToList();
        Assert.Equal(new[] { "b", "c", "a" }, r);   // newest first (REQ-4.5)
    }

    [Fact]
    public void Sort_by_price_ascending()
    {
        var r = Q(P("a", 300), P("b", 100), P("c", 200))
            .ApplySort([new SortOption("priceMinorUnits")]).Select(p => p.Name).ToList();
        Assert.Equal(new[] { "b", "c", "a" }, r);
    }

    // ---- typed ProductFilterDto.Parse (REQ-10, REQ-8.3) ----
    [Fact]
    public void ParseProductFilters_valid()
    {
        var dto = ProductFilterDto.Parse("""{"minPriceMinorUnits":100,"activeOnly":true}""");
        Assert.Equal(100, dto!.MinPriceMinorUnits);
        Assert.True(dto.ActiveOnly);
    }

    [Fact]
    public void ParseProductFilters_absent_is_null() => Assert.Null(ProductFilterDto.Parse(null));

    [Fact]
    public void ParseProductFilters_negative_is_rejected() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("""{"minPriceMinorUnits":-1}"""));

    [Fact]
    public void ParseProductFilters_malformed_is_rejected() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("not json"));

    // ---- tenant guard marker (REQ-7.2) ----
    [Fact]
    public void ListProductsQuery_is_tenant_scoped() =>
        Assert.True(typeof(ListProductsQuery).IsAssignableTo(typeof(ITenantScoped)));
}
