using System.Text.Json;
using BuildingBlocks.Application;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using Products.Domain;
using SharedKernel;
using SearchOption = BuildingBlocks.Application.SearchOption;

namespace Architecture.Tests;

/// <summary>
/// The merchant-scoped insurance-document SFS pipeline (in-memory <c>List.AsQueryable</c> cases;
/// <see cref="ProductSfs"/> is internal to Persistence.MerchantRuntime, so only a project with
/// InternalsVisibleTo can reference its extension methods directly). Proves the range/numeric operators
/// (gt/gte/lt/lte/between on totalPremiumAmount + createdAt), eq on the enum wire values and the bool,
/// the whitelist gating (including that <c>merchantId</c> is never filterable — REQ-7.3), the coercion
/// guard (wrong-typed value -> ArgumentException -> 400), the mandatory default sort, and the typed
/// <see cref="ProductFilterDto"/> parse. LIKE-escape + merchant narrowing over a real provider live in
/// ProductRepositoryListTests.
/// </summary>
public sealed class ProductSfsTests
{
    private static readonly Guid Merchant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Product P(
        string documentNo, decimal totalPremium, bool active = true, int dayOffset = 0,
        ProductGroup group = ProductGroup.VMI, DocumentType type = DocumentType.POLICY)
    {
        var p = Product.Create(
            new ProductInput(Merchant, group, type, documentNo, "100", "00098", Money.Of(totalPremium, "THB")),
            T0.AddDays(dayOffset));
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
    public void Merchant_id_is_never_filterable()
    {
        // merchantId is in no whitelist, so a filter naming it is dropped — SFS cannot widen merchant scope (REQ-7.3).
        var r = Q(P("a", 100)).ApplyFilters([new FilterOption("merchantId", FilterOperator.Equals, J("\"x\""))]);
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
    public void TotalPremium_comparison_operators(FilterOperator op, int expected)
    {
        var r = Q(P("a", 100), P("b", 200), P("c", 300))
            .ApplyFilters([new FilterOption("totalPremiumAmount", op, J("200"))]);
        Assert.Equal(expected, r.Count());
    }

    [Fact]
    public void TotalPremium_between_filter()
    {
        var r = Q(P("a", 100), P("b", 200), P("c", 300))
            .ApplyFilters([new FilterOption("totalPremiumAmount", FilterOperator.Between, Values: [J("150"), J("250")])]);
        Assert.Equal("b", Assert.Single(r).DocumentNo);
    }

    [Fact]
    public void Empty_between_values_is_dropped()
    {
        var r = Q(P("a", 100), P("b", 200))
            .ApplyFilters([new FilterOption("totalPremiumAmount", FilterOperator.Between, Values: [J("150")])]);
        Assert.Equal(2, r.Count());   // fewer than 2 values -> silent-drop (REQ-3.6)
    }

    [Fact]
    public void IsActive_eq_filter()
    {
        var r = Q(P("a", 100, active: true), P("b", 200, active: false))
            .ApplyFilters([new FilterOption("isActive", FilterOperator.Equals, J("true"))]);
        Assert.Equal("a", Assert.Single(r).DocumentNo);
    }

    // ---- enum eq filters (uppercase wire values, SP guide §2) ----
    [Fact]
    public void PaymentStatus_eq_filter()
    {
        var paid = P("paid-doc", 100);
        paid.MarkPaid(T0.AddDays(1));

        var r = Q(P("unpaid-doc", 100), paid)
            .ApplyFilters([new FilterOption("paymentStatus", FilterOperator.Equals, J("\"PAID\""))]);
        Assert.Equal("paid-doc", Assert.Single(r).DocumentNo);
    }

    [Fact]
    public void ProductGroup_eq_filter()
    {
        var r = Q(P("m", 100, group: ProductGroup.VMI), P("f", 100, group: ProductGroup.FIRE))
            .ApplyFilters([new FilterOption("productGroup", FilterOperator.Equals, J("\"FIRE\""))]);
        Assert.Equal("f", Assert.Single(r).DocumentNo);
    }

    [Fact]
    public void DocumentType_eq_filter()
    {
        var r = Q(P("p", 100), P("r", 100, type: DocumentType.RENEWAL))
            .ApplyFilters([new FilterOption("documentType", FilterOperator.Equals, J("\"RENEWAL\""))]);
        Assert.Equal("r", Assert.Single(r).DocumentNo);
    }

    // ---- coercion guard -> 400 (REQ-8.5) ----
    [Fact]
    public void Wrong_typed_totalPremium_throws_ArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            Q(P("a", 100)).ApplyFilters([new FilterOption("totalPremiumAmount", FilterOperator.GreaterThanOrEqual, J("\"abc\""))]));

    [Fact]
    public void Wrong_typed_isActive_throws_ArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            Q(P("a", 100)).ApplyFilters([new FilterOption("isActive", FilterOperator.Equals, J("\"nope\""))]));

    [Fact]
    public void Wrong_typed_createdAt_throws_ArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            Q(P("a", 100)).ApplyFilters([new FilterOption("createdAt", FilterOperator.GreaterThanOrEqual, J("123"))]));

    [Fact]
    public void Unknown_enum_value_throws_ArgumentException() =>
        Assert.Throws<ArgumentException>(() =>
            Q(P("a", 100)).ApplyFilters([new FilterOption("paymentStatus", FilterOperator.Equals, J("\"REFUNDED\""))]));

    // ---- sort ----
    [Fact]
    public void Default_sort_is_created_at_desc()
    {
        var r = Q(P("a", 1, dayOffset: 0), P("b", 1, dayOffset: 2), P("c", 1, dayOffset: 1))
            .ApplySort([]).Select(p => p.DocumentNo).ToList();
        Assert.Equal(new[] { "b", "c", "a" }, r);   // newest first (REQ-4.5)
    }

    [Fact]
    public void Default_sort_breaks_created_at_ties_by_id()
    {
        // All three share CreatedAt (dayOffset 0), so the fallback must fall through to the unique Id
        // tie-breaker for deterministic paging (REQ-4.5) — without it the order would be arbitrary.
        var a = P("a", 1);
        var b = P("b", 1);
        var c = P("c", 1);

        var ordered = Q(a, b, c).ApplySort([]).Select(p => p.Id).ToList();

        Assert.Equal(new[] { a.Id, b.Id, c.Id }.OrderByDescending(id => id).ToList(), ordered);
    }

    [Fact]
    public void Sort_by_totalPremium_ascending()
    {
        var r = Q(P("a", 300), P("b", 100), P("c", 200))
            .ApplySort([new SortOption("totalPremiumAmount")]).Select(p => p.DocumentNo).ToList();
        Assert.Equal(new[] { "b", "c", "a" }, r);
    }

    // ---- typed ProductFilterDto.Parse (full matrix lives in Products.Tests.ProductFilterDtoTests) ----
    [Fact]
    public void ParseProductFilters_valid()
    {
        var dto = ProductFilterDto.Parse("""{"paymentStatus":"UNPAID","productGroup":"VMI"}""");
        Assert.Equal(PaymentStatus.UNPAID, dto!.PaymentStatus);
        Assert.Equal(ProductGroup.VMI, dto.ProductGroup);
    }

    [Fact]
    public void ParseProductFilters_absent_is_null() => Assert.Null(ProductFilterDto.Parse(null));

    [Fact]
    public void ParseProductFilters_inverted_range_is_rejected() =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse("""{"coverageStartFrom":"2026-07-02","coverageStartTo":"2026-07-01"}"""));

    [Fact]
    public void ParseProductFilters_malformed_is_rejected() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("not json"));

    // ---- merchant guard marker (REQ-7.2) ----
    [Fact]
    public void ListProductsQuery_is_tenant_scoped() =>
        Assert.True(typeof(ListProductsQuery).IsAssignableTo(typeof(IMerchantScoped)));
}
