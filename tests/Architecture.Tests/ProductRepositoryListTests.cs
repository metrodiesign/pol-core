using System.Globalization;
using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using Products.Domain;

namespace Architecture.Tests;

/// <summary>
/// <see cref="ProductRepository.ListAsync"/> over <see cref="MerchantRuntimeDbContext"/> backed by in-memory
/// SQLite (the class is internal, so only a project with InternalsVisibleTo can construct it directly).
/// Proves the §2 input surface of <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> as built: the
/// merchant floor plus required <c>@SaleCode</c> narrowing (REQ-3.3), <c>@PaymentStatus</c> defaulting to
/// UNPAID with <c>ALL</c> meaning "do not filter" (REQ-3.1/3.2), the search window (REQ-6.1/6.2) driven by an
/// injected <see cref="IClock"/> so the result does not depend on the wall clock, the per-row Motor gate on
/// the licence plate (REQ-5.1/5.2), <c>OrderBy(DocumentNo)</c> (REQ-7.2), total-after-filter paging, and the
/// full 32-field projection (REQ-8.1).
/// </summary>
public sealed class ProductRepositoryListTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>Frozen "today" for every window assertion — matches the table in the spec's HANDOFF.</summary>
    private static readonly DateTime Today = new(2026, 7, 30, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public ProductRepositoryListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    // The caller passes MerchantId explicitly in the query; bind an arbitrary actor so the ambient read floor
    // does not also filter these rows out from under the explicit query.
    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private ProductRepository Repo() => new(NewContext(), new FixedClock(Today.AddHours(9)));

    private void Seed(params Product[] products)
    {
        using var seed = NewContext();
        seed.Set<Product>().AddRange(products);
        seed.SaveChanges();
    }

    /// <summary>A document that is inside the default 6-month window unless the caller says otherwise.</summary>
    private static Product Prod(
        Guid merchant, string documentNo, decimal totalPremium = 100m, string saleCode = "00098",
        ProductGroup group = ProductGroup.VMI, DocumentType type = DocumentType.POLICY,
        DateTime? startDate = null, DateTime? endDate = null, string? showName = null, string? plate = null) =>
        Product.Create(new ProductInput(
            merchant, group, type, documentNo, saleCode, totalPremium,
            StartDate: startDate ?? Today.AddDays(-1), EndDate: endDate,
            ShowName: showName, LicensePlateNumber: plate));

    private static ListProductsQuery Query(
        ProductFilterDto? filters = null, Guid? merchantId = null, int page = 1, int limit = 25) =>
        new()
        {
            MerchantId = merchantId ?? MerchantA,
            ProductFilters = filters ?? new ProductFilterDto { SaleCode = "00098" },
            Page = page,
            Limit = limit,
        };

    [Fact]
    public async Task Lists_only_the_bound_merchants_documents()
    {
        Seed(Prod(MerchantA, "a1"), Prod(MerchantA, "a2"), Prod(MerchantB, "b1"));

        var page = await Repo().ListAsync(Query(), CancellationToken.None);

        Assert.Equal(2, page.Total);                              // merchant B's row is never counted
        Assert.Equal(new[] { "a1", "a2" }, page.Items.Select(i => i.DocumentNo));
    }

    [Fact]
    public async Task SaleCode_narrows_the_result_exactly()
    {
        Seed(
            Prod(MerchantA, "mine-1", saleCode: "00098"),
            Prod(MerchantA, "mine-2", saleCode: "00098"),
            Prod(MerchantA, "theirs", saleCode: "00099"));

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00099" }), CancellationToken.None);

        Assert.Equal("theirs", Assert.Single(page.Items).DocumentNo);
    }

    [Fact]
    public async Task An_absent_paymentStatus_returns_only_UNPAID_documents()
    {
        var paid = Prod(MerchantA, "paid-doc");
        paid.MarkPaid(Today);
        Seed(Prod(MerchantA, "unpaid-doc"), paid);

        var page = await Repo().ListAsync(Query(), CancellationToken.None);

        Assert.Equal("unpaid-doc", Assert.Single(page.Items).DocumentNo);
    }

    [Fact]
    public async Task ALL_returns_both_paid_and_unpaid_documents()
    {
        var paid = Prod(MerchantA, "paid-doc");
        paid.MarkPaid(Today);
        Seed(Prod(MerchantA, "unpaid-doc"), paid);

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00098", PaymentStatus = "ALL" }), CancellationToken.None);

        Assert.Equal(new[] { "paid-doc", "unpaid-doc" }, page.Items.Select(i => i.DocumentNo));
    }

    [Fact]
    public async Task PAID_returns_only_paid_documents()
    {
        var paid = Prod(MerchantA, "paid-doc");
        paid.MarkPaid(Today);
        Seed(Prod(MerchantA, "unpaid-doc"), paid);

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00098", PaymentStatus = "PAID" }), CancellationToken.None);

        Assert.Equal("paid-doc", Assert.Single(page.Items).DocumentNo);
    }

    // Cases A-K of the search-window table in the spec's HANDOFF (today = 2026-07-30). A general document is
    // filtered on StartDate looking back 6 months with no upper bound; a RENEWAL is filtered on EndDate over
    // the half-open [today, today + 2 months). NULL never matches either side.
    [Theory]
    [InlineData("A", DocumentType.POLICY, "2026-07-01", null, true)]
    [InlineData("B", DocumentType.POLICY, "2026-01-30", null, true)]     // lower bound inclusive
    [InlineData("C", DocumentType.POLICY, "2026-01-29", null, false)]    // older than 6 months
    [InlineData("D", DocumentType.POLICY, "2027-01-01", null, true)]     // no upper bound
    [InlineData("E", DocumentType.POLICY, null, null, false)]           // NULL StartDate
    [InlineData("F", DocumentType.RENEWAL, "2020-01-01", "2026-07-30", true)]   // lower bound = today
    [InlineData("G", DocumentType.RENEWAL, "2020-01-01", "2026-09-29", true)]
    [InlineData("H", DocumentType.RENEWAL, "2020-01-01", "2026-09-30", false)]  // upper bound half-open
    [InlineData("I", DocumentType.RENEWAL, "2020-01-01", "2026-07-29", false)]  // already expired
    [InlineData("J", DocumentType.RENEWAL, "2026-01-01", "2026-08-15", true)]   // StartDate is irrelevant
    [InlineData("K", DocumentType.RENEWAL, "2026-07-01", null, false)]          // NULL EndDate
    public async Task The_search_window_admits_only_documents_in_range(
        string label, DocumentType type, string? startDate, string? endDate, bool expectedInWindow)
    {
        // Built without the Prod helper: these cases need a genuinely NULL StartDate/EndDate, which the
        // helper's in-window default would mask.
        Seed(Product.Create(new ProductInput(
            MerchantA, ProductGroup.VMI, type, label, "00098", 100m,
            StartDate: Day(startDate), EndDate: Day(endDate))));

        var page = await Repo().ListAsync(Query(), CancellationToken.None);

        Assert.Equal(expectedInWindow ? 1 : 0, page.Total);
    }

    [Fact]
    public async Task The_window_applies_even_when_the_client_filters_on_something_else()
    {
        Seed(Prod(MerchantA, "too-old", startDate: new DateTime(2025, 1, 1)));

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00098", PaymentStatus = "ALL", ProductGroup = ProductGroup.VMI }),
            CancellationToken.None);

        Assert.Empty(page.Items);
    }

    // REQ-5.1/5.2: the licence plate joins the search predicate only on CMI/VMI rows.
    [Theory]
    [InlineData(ProductGroup.CMI, true)]
    [InlineData(ProductGroup.VMI, true)]
    [InlineData(ProductGroup.FIRE, false)]
    [InlineData(ProductGroup.MISC, false)]
    public async Task A_licence_plate_is_searchable_on_Motor_rows_only(ProductGroup group, bool expectedMatch)
    {
        Seed(Prod(MerchantA, $"doc-{group}", group: group, plate: "กข 1234"));

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00098", SearchText = "กข 1234" }), CancellationToken.None);

        Assert.Equal(expectedMatch ? 1 : 0, page.Total);
    }

    [Fact]
    public async Task Search_also_matches_the_document_and_policy_numbers_on_non_Motor_rows()
    {
        Seed(Prod(MerchantA, "FIRE-77", group: ProductGroup.FIRE));

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00098", SearchText = "FIRE-77" }), CancellationToken.None);

        Assert.Equal("FIRE-77", Assert.Single(page.Items).DocumentNo);
    }

    [Fact]
    public async Task Search_escapes_wildcards()
    {
        Seed(Prod(MerchantA, "50% off"), Prod(MerchantA, "500 baht"));

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00098", SearchText = "50%" }), CancellationToken.None);

        // "%" is a literal, so "500 baht" does not match.
        Assert.Equal("50% off", Assert.Single(page.Items).DocumentNo);
    }

    [Fact]
    public async Task Insured_name_matches_partially()
    {
        Seed(
            Prod(MerchantA, "d1", showName: "สมชาย ใจดี"),
            Prod(MerchantA, "d2", showName: "สมหญิง รักดี"),
            Prod(MerchantA, "d3"));   // ShowName null -> never matches

        var page = await Repo().ListAsync(
            Query(new ProductFilterDto { SaleCode = "00098", InsuredName = "สมชาย" }), CancellationToken.None);

        Assert.Equal("d1", Assert.Single(page.Items).DocumentNo);
    }

    [Fact]
    public async Task Returns_a_paged_slice_ordered_by_DocumentNo_with_the_total_after_filtering()
    {
        var paid = Prod(MerchantA, "p0");
        paid.MarkPaid(Today);
        Seed(
            Prod(MerchantA, "p3"), Prod(MerchantA, "p1"), Prod(MerchantA, "p5"),
            Prod(MerchantA, "p2"), Prod(MerchantA, "p4"), paid);

        var page = await Repo().ListAsync(Query(page: 1, limit: 2), CancellationToken.None);

        Assert.Equal(5, page.Total);                 // the PAID row is filtered out before counting
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(new[] { "p1", "p2" }, page.Items.Select(i => i.DocumentNo));

        var second = await Repo().ListAsync(Query(page: 2, limit: 2), CancellationToken.None);
        Assert.Equal(new[] { "p3", "p4" }, second.Items.Select(i => i.DocumentNo));
    }

    [Fact]
    public async Task Projects_every_field_of_the_result_set()
    {
        var start = new DateTime(2026, 7, 1);
        var end = new DateTime(2027, 6, 30);
        Seed(Product.Create(new ProductInput(
            MerchantA, ProductGroup.CMI, DocumentType.POLICY, "full-1", "00098", 15900m,
            PolicyYear: "69", ReferenceBranch: "100", ReferencePre: "pre", PolicySequenceNo: "seq",
            ReferenceYear: "69", ReferenceNo: "ref", PolicyBranch: "branch", PolicyType: "type",
            SaleFullName: "สมชาย ขาย", BrokerCode: "BK", BrokerName: "โบรก", PolicyNumber: "POL-1",
            ApplicationNumber: "APP-1", PreviousPolicyNumber: "POL-0", EndorsementNumber: "END-1",
            StartDate: start, EndDate: end, ShowName: "ผู้เอาประกัน", LicensePlateNumber: "กข 1234",
            NetPremium: 14000m, Stamp: 10m, TaxVat: 980m, CommissionAmount: 500m, CommissionPercent: 12.5m)));

        var item = Assert.Single((await Repo().ListAsync(Query(), CancellationToken.None)).Items);

        Assert.NotEqual(Guid.Empty, item.Id);
        Assert.Equal(ProductGroup.CMI, item.ProductGroup);
        Assert.Equal(DocumentType.POLICY, item.DocumentType);
        Assert.Equal("full-1", item.DocumentNo);
        Assert.Equal("69", item.PolicyYear);
        Assert.Equal("100", item.ReferenceBranch);
        Assert.Equal("pre", item.ReferencePre);
        Assert.Equal("seq", item.PolicySequenceNo);
        Assert.Equal("69", item.ReferenceYear);
        Assert.Equal("ref", item.ReferenceNo);
        Assert.Equal("branch", item.PolicyBranch);
        Assert.Equal("type", item.PolicyType);
        Assert.Equal("00098", item.SaleCode);
        Assert.Equal("สมชาย ขาย", item.SaleFullName);
        Assert.Equal("BK", item.BrokerCode);
        Assert.Equal("โบรก", item.BrokerName);
        Assert.Equal("POL-1", item.PolicyNumber);
        Assert.Equal("APP-1", item.ApplicationNumber);
        Assert.Equal("POL-0", item.PreviousPolicyNumber);
        Assert.Equal("END-1", item.EndorsementNumber);
        Assert.Equal(start, item.StartDate);
        Assert.Equal(end, item.EndDate);
        Assert.Equal("ผู้เอาประกัน", item.ShowName);
        Assert.Equal(14000m, item.NetPremium);
        Assert.Equal(10m, item.Stamp);
        Assert.Equal(980m, item.TaxVat);
        Assert.Equal(15900m, item.TotalPremium);
        Assert.Equal(12.5m, item.CommissionPercent);
        Assert.Equal(500m, item.CommissionAmount);
        Assert.Null(item.PaidDate);
        Assert.Equal("กข 1234", item.LicensePlateNumber);
        Assert.Equal(PaymentStatus.UNPAID, item.PaymentStatus);
        Assert.Equal(InsuranceType.Motor, item.InsuranceType);   // computed, never projected
    }

    private static DateTime? Day(string? isoDate) =>
        isoDate is null ? null : DateTime.Parse(isoDate, CultureInfo.InvariantCulture);

    public void Dispose() => _connection.Dispose();

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
