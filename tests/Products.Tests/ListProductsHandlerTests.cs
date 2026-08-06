using BuildingBlocks.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Products.Application;
using Products.Application.Ports;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// <see cref="ListProductsHandler"/> — the read-live orchestration (products-external-source-of-truth REQ-1,
/// REQ-5.8, REQ-5.9, REQ-5.15) with a fake gateway and a fake sold-check probe: which procedure a filter routes
/// to and what it is asked, that the answered envelope is the procedure's own numbers, that a document already
/// sold on this platform is dropped from an UNPAID page and flagged on an ALL/PAID one, and that the probe reads
/// once for the whole page. There is no repository and no upsert any more — the catalogue is read-only.
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

        public Task<SpDocumentItem?> LookupAsync(SpDocumentLookupRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>Answers the sold-check from two configurable sets, keyed by DocumentNo. Records how many times
    /// it was called (REQ-5.15 — exactly once per page) and with which keys.</summary>
    private sealed class FakeProbe : IDocumentSaleProbe
    {
        public HashSet<string> Sold { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> InFlight { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public int Calls { get; private set; }
        public IReadOnlyCollection<DocumentKey>? LastKeys { get; private set; }

        public Task<IReadOnlyList<DocumentSaleStatus>> ProbeAsync(
            IReadOnlyCollection<DocumentKey> keys, CancellationToken cancellationToken)
        {
            Calls++;
            LastKeys = keys;
            var result = new List<DocumentSaleStatus>();
            foreach (var key in keys)
            {
                if (Sold.Contains(key.DocumentNo))
                    result.Add(new DocumentSaleStatus(key, DocumentSaleState.Sold, Guid.NewGuid()));
                else if (InFlight.Contains(key.DocumentNo))
                    result.Add(new DocumentSaleStatus(key, DocumentSaleState.PaymentInFlight, Guid.NewGuid()));
            }
            return Task.FromResult<IReadOnlyList<DocumentSaleStatus>>(result);
        }
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
        new() { SaleCode = SaleCode, ProductFilters = filters, Page = page, Limit = limit };

    private static ProductFilterDto Filters(
        ProductGroup? productGroup = null, InsuranceType? insuranceType = null, string? countMode = null,
        string? paymentStatus = null) =>
        new()
        {
            ProductGroup = productGroup,
            InsuranceType = insuranceType,
            CountMode = countMode,
            PaymentStatus = paymentStatus,
        };

    private static Task<ProductPage> HandleAsync(
        ISpDocumentGateway gateway, IDocumentSaleProbe probe, ListProductsQuery query) =>
        new ListProductsHandler(gateway, probe, NullLogger<ListProductsHandler>.Instance)
            .Handle(query, CancellationToken.None).AsTask();

    // The routing table of design.md, row by row (REQ-3.2). The two catalogues are separate procedures
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

        await HandleAsync(gateway, new FakeProbe(), Query(Filters(group, side)));

        Assert.Equal(expectedTarget, gateway.Request!.Target);
        Assert.Equal(expectedProductGroup, gateway.Request.ProductGroup);
    }

    // A group and a side that disagree is a 400, never a silent pick of one of them.
    [Theory]
    [InlineData(ProductGroup.FIRE, InsuranceType.Motor)]
    [InlineData(ProductGroup.MISC, InsuranceType.Motor)]
    [InlineData(ProductGroup.CMI, InsuranceType.NonMotor)]
    [InlineData(ProductGroup.VMI, InsuranceType.NonMotor)]
    public async Task A_product_group_that_contradicts_the_insurance_type_is_rejected(
        ProductGroup group, InsuranceType side) =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandleAsync(new FakeGateway(FullPage), new FakeProbe(), Query(Filters(group, side))));

    // Neither given: there is no default side, and fanning out to both cannot produce one page.
    [Fact]
    public async Task A_filter_with_neither_a_product_group_nor_an_insurance_type_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandleAsync(new FakeGateway(FullPage), new FakeProbe(), Query(Filters())));

    [Fact]
    public async Task The_request_carries_the_server_sale_code_and_the_wire_defaults()
    {
        var gateway = new FakeGateway(FullPage);

        await HandleAsync(gateway, new FakeProbe(),
            Query(Filters(insuranceType: InsuranceType.Motor), page: 3, limit: 10));

        var request = gateway.Request!;
        Assert.Equal(SaleCode, request.SaleCode);   // REQ-4.8 — the query's server-supplied sale code, not the client's
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

        await HandleAsync(gateway, new FakeProbe(),
            Query(Filters(insuranceType: InsuranceType.Motor, paymentStatus: filter)));

        Assert.Equal(expected, gateway.Request!.PaymentStatus);
    }

    // REQ-1.5 — the envelope is copied, not recomputed; FAST really means "no totals", not zero.
    [Fact]
    public async Task The_envelope_is_the_procedures_own_numbers()
    {
        var page = new SpPaginationMetadata(
            TotalRows: null, TotalPages: null, PageNo: 2, PageSize: 25,
            HasNextPage: true, HasPreviousPage: true, CountMode: "FAST", SearchWindowMonths: 6);
        var gateway = new FakeGateway(page, Row("doc-1"));

        var result = await HandleAsync(gateway, new FakeProbe(),
            Query(Filters(insuranceType: InsuranceType.Motor, countMode: "FAST"), page: 2));

        Assert.Null(result.TotalRows);
        Assert.Null(result.TotalPages);
        Assert.Equal(2, result.PageNo);
        Assert.True(result.HasNextPage);
        Assert.Equal("FAST", result.CountMode);
        Assert.Equal(6, result.SearchWindowMonths);
    }

    // REQ-1.4 — the answer is in the procedure's order; nothing here re-sorts.
    [Fact]
    public async Task Items_keep_the_order_the_procedure_returned()
    {
        var gateway = new FakeGateway(FullPage, Row("z-doc"), Row("a-doc"), Row("m-doc"));

        var result = await HandleAsync(gateway, new FakeProbe(),
            Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.Equal(["z-doc", "a-doc", "m-doc"], result.Items.Select(i => i.DocumentNo));
    }

    // REQ-1.6 — an unusable row drops out of the page while the rest still answers; the totals stay the
    // procedure's, so the page can legitimately be shorter than TotalRows implies.
    [Fact]
    public async Task An_unusable_row_is_dropped_and_the_rest_of_the_page_still_answers()
    {
        var gateway = new FakeGateway(FullPage,
            Row("good-1"),
            Row("no-premium", totalPremium: null),
            Row("unknown-source", sourceSystem: "ZZZ"),
            Row("blank-sale-code", saleCode: "   "),
            Row("good-2"));

        var result = await HandleAsync(gateway, new FakeProbe(), Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.Equal(["good-1", "good-2"], result.Items.Select(i => i.DocumentNo));
        Assert.Equal(3, result.TotalRows);   // the procedure counted 3; the page answers 2
    }

    // REQ-1.7 — two rows in one page claiming the same DocumentNo (compared trimmed + case-insensitively per
    // REQ-2.3) collapse to the FIRST; the later duplicate is dropped so a document is never listed twice at two
    // prices. The procedure's totals are left as it counted them, exactly like the drop-unusable case.
    [Fact]
    public async Task A_DocumentNo_repeated_in_the_page_keeps_only_the_first_row()
    {
        var gateway = new FakeGateway(FullPage,
            Row("dup-doc", totalPremium: 15900m),
            Row("DUP-DOC", totalPremium: 999m),   // same number, different case + price — must not survive
            Row("other"));

        var result = await HandleAsync(gateway, new FakeProbe(), Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.Equal(["dup-doc", "other"], result.Items.Select(i => i.DocumentNo));
        Assert.Equal(15900m, result.Items[0].TotalPremium);   // the FIRST row's price wins
        Assert.Equal(3, result.TotalRows);   // the procedure counted 3; the page answers 2
    }

    // REQ-5.15 — the sold-check reads once for the whole page, with the mapped documents' keys, not one call
    // per row.
    [Fact]
    public async Task The_sold_check_reads_once_for_the_whole_page()
    {
        var probe = new FakeProbe();
        var gateway = new FakeGateway(FullPage, Row("doc-1"), Row("doc-2"), Row("doc-3"));

        await HandleAsync(gateway, probe, Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.Equal(1, probe.Calls);
        Assert.Equal(["doc-1", "doc-2", "doc-3"], probe.LastKeys!.Select(k => k.DocumentNo));
    }

    // REQ-5.8 — a search asking for UNPAID drops the rows an order on this platform already sold; the
    // procedure's totals are left exactly as it counted them (REQ-5.5).
    [Fact]
    public async Task A_document_sold_here_is_dropped_from_an_UNPAID_page()
    {
        var probe = new FakeProbe { Sold = { "sold-here" } };
        var gateway = new FakeGateway(FullPage, Row("still-for-sale"), Row("sold-here"));

        var result = await HandleAsync(gateway, probe, Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.Equal(["still-for-sale"], result.Items.Select(i => i.DocumentNo));
        Assert.Equal(3, result.TotalRows);   // unchanged
    }

    // REQ-5.9 — an ALL/PAID search keeps every row and just flags whether this platform sold it, without
    // touching the upstream's own paymentStatus.
    [Theory]
    [InlineData("ALL")]
    [InlineData("PAID")]
    public async Task A_page_that_did_not_ask_for_UNPAID_keeps_the_sold_document_and_flags_it(string paymentStatus)
    {
        var probe = new FakeProbe { Sold = { "sold-here" } };
        var gateway = new FakeGateway(FullPage, Row("sold-here"));

        var result = await HandleAsync(gateway, probe,
            Query(Filters(insuranceType: InsuranceType.Motor, paymentStatus: paymentStatus)));

        var item = Assert.Single(result.Items);
        Assert.Equal("sold-here", item.DocumentNo);
        Assert.True(item.SoldByPlatform);
        Assert.Equal(PaymentStatus.UNPAID, item.PaymentStatus);   // the upstream's own value is untouched (REQ-5.9)
    }

    // REQ-5.9 — a document nobody has bought here is flagged not-sold.
    [Fact]
    public async Task A_document_not_sold_here_is_flagged_not_sold()
    {
        var gateway = new FakeGateway(FullPage, Row("free"));

        var result = await HandleAsync(gateway, new FakeProbe(), Query(Filters(insuranceType: InsuranceType.Motor)));

        Assert.False(Assert.Single(result.Items).SoldByPlatform);
    }

    [Fact]
    public async Task An_empty_page_answers_with_no_items_and_the_procedures_envelope()
    {
        var page = new SpPaginationMetadata(28, 2, 99, 25, HasNextPage: false, HasPreviousPage: true, "EXACT", 6);

        var result = await HandleAsync(new FakeGateway(page), new FakeProbe(),
            Query(Filters(insuranceType: InsuranceType.Motor), page: 99));

        Assert.Empty(result.Items);
        Assert.Equal(28, result.TotalRows);
        Assert.True(result.HasPreviousPage);
    }
}
