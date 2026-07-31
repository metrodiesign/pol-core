using Microsoft.Extensions.Logging.Abstractions;
using Products.Application;
using Products.Application.Ports;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// <see cref="ListProductsHandler"/> — the cutover orchestration (products-sp-gateway REQ-6.1-6.4, 7.1, 7.2,
/// 7.7, 7.9, 8.1, 8.2) with a fake gateway and a fake repository: which procedure a filter routes to and what
/// it is asked, which rows reach the upsert, and that the answered envelope is the procedure's own numbers
/// rather than anything recomputed here.
/// </summary>
public sealed class ListProductsHandlerTests
{
    private const string SaleCode = "77001";

    private static readonly SpPaginationMetadata FullPage =
        new(3, 1, 1, 25, HasNextPage: false, HasPreviousPage: false, "EXACT", 6);

    private sealed class FakeGateway(SpPaginationMetadata page, params SpDocumentItem[] items) : ISpDocumentGateway
    {
        public SpDocumentSearchRequest? Request { get; private set; }

        public Task<SpDocumentSearchResult> SearchAsync(SpDocumentSearchRequest request, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new SpDocumentSearchResult(page, items));
        }
    }

    private sealed class FakeRepository : IProductRepository
    {
        public IReadOnlyList<ProductInput> Upserted { get; private set; } = [];

        public Task<IReadOnlyList<Product>> UpsertByDocumentNoAsync(
            IReadOnlyList<ProductInput> inputs, CancellationToken cancellationToken)
        {
            Upserted = inputs;
            return Task.FromResult<IReadOnlyList<Product>>([.. inputs.Select(Product.Create)]);
        }

        public void Add(Product product) => throw new NotSupportedException();
        public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>A row that maps cleanly; <paramref name="documentNo"/> is what the assertions follow.</summary>
    private static SpDocumentItem Row(string documentNo, string sourceSystem = "VMI",
        string? saleCode = SaleCode, decimal? totalPremium = 15900m,
        string paymentStatus = "UNPAID", DateTime? paidDate = null) =>
        new("Motor", sourceSystem, "POLICY", documentNo,
            null, null, null, null, null, null, null, null,
            saleCode, null, null, null, null, null, null, null,
            null, null, null,
            null, null, null, totalPremium, null, null, paidDate,
            null, paymentStatus);

    private static ListProductsQuery Query(ProductFilterDto filters, int page = 1, int limit = 25) =>
        new() { ProductFilters = filters, Page = page, Limit = limit };

    private static ProductFilterDto Filters(
        ProductGroup? productGroup = null, InsuranceType? insuranceType = null, string? countMode = null,
        string? paymentStatus = null) =>
        new()
        {
            SaleCode = SaleCode,
            ProductGroup = productGroup,
            InsuranceType = insuranceType,
            CountMode = countMode,
            PaymentStatus = paymentStatus,
        };

    private static Task<ProductPage> HandleAsync(
        ISpDocumentGateway gateway, IProductRepository repository, ListProductsQuery query) =>
        new ListProductsHandler(gateway, repository, NullLogger<ListProductsHandler>.Instance)
            .Handle(query, CancellationToken.None).AsTask();

    // The routing table of design.md, row by row (REQ-6.1-6.4). The two catalogues are separate procedures
    // with separate paging, so exactly one side has to be resolvable from the filter.
    [Theory]
    [InlineData(ProductGroup.CMI, null, InsuranceType.Motor, "CMI")]
    [InlineData(ProductGroup.VMI, InsuranceType.Motor, InsuranceType.Motor, "VMI")]
    [InlineData(ProductGroup.FIRE, null, InsuranceType.NonMotor, "FIRE")]
    [InlineData(ProductGroup.MISC, InsuranceType.NonMotor, InsuranceType.NonMotor, "MISC")]
    [InlineData(null, InsuranceType.Motor, InsuranceType.Motor, "ALL")]
    [InlineData(null, InsuranceType.NonMotor, InsuranceType.NonMotor, "ALL")]
    public async Task A_filter_routes_to_one_side_with_the_product_group_it_implies(
        ProductGroup? group, InsuranceType? side, InsuranceType expectedTarget, string expectedProductGroup)
    {
        var gateway = new FakeGateway(FullPage);

        await HandleAsync(gateway, new FakeRepository(), Query(Filters(group, side)));

        Assert.Equal(expectedTarget, gateway.Request!.Target);
        Assert.Equal(expectedProductGroup, gateway.Request.ProductGroup);
    }

    // REQ-6.2 — a group and a side that disagree is a 400, never a silent pick of one of them.
    [Theory]
    [InlineData(ProductGroup.FIRE, InsuranceType.Motor)]
    [InlineData(ProductGroup.MISC, InsuranceType.Motor)]
    [InlineData(ProductGroup.CMI, InsuranceType.NonMotor)]
    [InlineData(ProductGroup.VMI, InsuranceType.NonMotor)]
    public async Task A_product_group_that_contradicts_the_insurance_type_is_rejected(
        ProductGroup group, InsuranceType side) =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandleAsync(new FakeGateway(FullPage), new FakeRepository(), Query(Filters(group, side))));

    // REQ-6.3 — neither given: there is no default side, and fanning out to both cannot produce one page.
    [Fact]
    public async Task A_filter_with_neither_a_product_group_nor_an_insurance_type_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandleAsync(new FakeGateway(FullPage), new FakeRepository(), Query(Filters())));

    [Fact]
    public async Task The_request_carries_the_paging_and_the_wire_defaults()
    {
        var gateway = new FakeGateway(FullPage);

        await HandleAsync(gateway, new FakeRepository(),
            Query(Filters(insuranceType: InsuranceType.Motor), page: 3, limit: 10));

        var request = gateway.Request!;
        Assert.Equal(SaleCode, request.SaleCode);
        Assert.Equal(3, request.PageNo);
        Assert.Equal(10, request.PageSize);
        Assert.Equal("UNPAID", request.PaymentStatus);   // absent = UNPAID (§2)
        Assert.Equal("ALL", request.DocumentType);
        Assert.Equal("EXACT", request.CountMode);
    }

    [Theory]
    [InlineData("ALL", "ALL")]
    [InlineData("PAID", "PAID")]
    [InlineData("UNPAID", "UNPAID")]
    public async Task The_payment_status_travels_as_its_wire_value(string filter, string expected)
    {
        var gateway = new FakeGateway(FullPage);

        await HandleAsync(gateway, new FakeRepository(),
            Query(Filters(insuranceType: InsuranceType.Motor, paymentStatus: filter)));

        Assert.Equal(expected, gateway.Request!.PaymentStatus);
    }

    [Fact]
    public async Task The_count_mode_travels_as_asked()
    {
        var gateway = new FakeGateway(FullPage);

        await HandleAsync(gateway, new FakeRepository(),
            Query(Filters(insuranceType: InsuranceType.Motor, countMode: "FAST")));

        Assert.Equal("FAST", gateway.Request!.CountMode);
    }

    // REQ-8.1/8.2 — the envelope is copied, not recomputed; FAST really means "no totals", not zero.
    [Fact]
    public async Task The_envelope_is_the_procedures_own_numbers()
    {
        var page = new SpPaginationMetadata(
            TotalRows: null, TotalPages: null, PageNo: 2, PageSize: 25,
            HasNextPage: true, HasPreviousPage: true, CountMode: "FAST", SearchWindowMonths: 6);
        var gateway = new FakeGateway(page, Row("doc-1"));

        var result = await HandleAsync(gateway, new FakeRepository(),
            Query(Filters(insuranceType: InsuranceType.Motor, countMode: "FAST"), page: 2));

        Assert.Null(result.TotalRows);
        Assert.Null(result.TotalPages);
        Assert.Equal(2, result.PageNo);
        Assert.Equal(25, result.PageSize);
        Assert.True(result.HasNextPage);
        Assert.True(result.HasPreviousPage);
        Assert.Equal("FAST", result.CountMode);
        Assert.Equal(6, result.SearchWindowMonths);
    }

    // REQ-7.9 — the answer is in the procedure's order; nothing here re-sorts (the procedure orders by
    // DocumentNo under a Thai collation, which no local OrderBy would reproduce).
    [Fact]
    public async Task Items_keep_the_order_the_procedure_returned()
    {
        var gateway = new FakeGateway(FullPage, Row("z-doc"), Row("a-doc"), Row("m-doc"));

        var result = await HandleAsync(gateway, new FakeRepository(),
            Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.Equal(["z-doc", "a-doc", "m-doc"], result.Items.Select(i => i.DocumentNo));
    }

    // REQ-7.7 — an unusable row drops out of the page (and out of the upsert) while the rest still answers;
    // the totals stay the procedure's, so the page can legitimately be shorter than TotalRows implies.
    [Fact]
    public async Task An_unusable_row_is_dropped_and_the_rest_of_the_page_still_answers()
    {
        var repository = new FakeRepository();
        var gateway = new FakeGateway(FullPage,
            Row("good-1"),
            Row("no-premium", totalPremium: null),
            Row("unknown-source", sourceSystem: "ZZZ"),
            Row("blank-sale-code", saleCode: "   "),
            Row("good-2"));

        var result = await HandleAsync(gateway, repository, Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.Equal(["good-1", "good-2"], result.Items.Select(i => i.DocumentNo));
        Assert.Equal(["good-1", "good-2"], repository.Upserted.Select(i => i.DocumentNo));
        Assert.Equal(3, result.TotalRows);   // the procedure counted 3; the page answers 2
    }

    // REQ-7.2 — every usable row is handed to the upsert, carrying the wire payment state (B1: a row that
    // arrives PAID must not be stored UNPAID).
    [Fact]
    public async Task The_upsert_receives_the_mapped_rows_with_their_wire_payment_state()
    {
        var paidAt = new DateTime(2026, 7, 20, 9, 15, 0);
        var repository = new FakeRepository();
        var gateway = new FakeGateway(FullPage,
            Row("paid-doc", paymentStatus: "PAID", paidDate: paidAt),
            Row("unpaid-doc"));

        await HandleAsync(gateway, repository, Query(Filters(insuranceType: InsuranceType.Motor)));

        var paid = repository.Upserted[0];
        Assert.Equal(PaymentStatus.PAID, paid.PaymentStatus);
        Assert.Equal(paidAt, paid.PaidDate);
        Assert.Equal(PaymentStatus.UNPAID, repository.Upserted[1].PaymentStatus);
    }

    [Fact]
    public async Task An_empty_page_answers_with_no_items_and_the_procedures_envelope()
    {
        var page = new SpPaginationMetadata(28, 2, 99, 25, HasNextPage: false, HasPreviousPage: true, "EXACT", 6);

        var result = await HandleAsync(new FakeGateway(page), new FakeRepository(),
            Query(Filters(insuranceType: InsuranceType.Motor), page: 99));

        Assert.Empty(result.Items);
        Assert.Equal(28, result.TotalRows);
        Assert.True(result.HasPreviousPage);
    }
}
