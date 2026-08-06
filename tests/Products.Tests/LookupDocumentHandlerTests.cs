using Microsoft.Extensions.Logging.Abstractions;
using Products.Application;
using Products.Application.Ports;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// products-external-source-of-truth REQ-3 (+ REQ-2.5) — the single-document lookup. Two seams are proven here
/// without SQL: <see cref="LookupDocumentHandler"/> against a fake gateway (boundary validation, trimming, and
/// that the row the upstream returns is the one that wins), and <see cref="SpDocumentMatch.SelectExactlyOne"/> as
/// a pure function (the REQ-2.3 exact-match filter over the LIKE's prefix hits, and the 0/1/many decision that the
/// real adapter delegates to it). The live SP path with a real "<c>/</c> + Thai" number is proven in
/// <c>Integration.Tests</c>.
/// </summary>
public sealed class LookupDocumentHandlerTests
{
    private const string SaleCode = "77001";

    /// <summary>A §5.2 row that maps cleanly; <paramref name="documentNo"/> and <paramref name="sourceSystem"/>
    /// are what the assertions follow.</summary>
    private static SpDocumentItem Row(string documentNo, string sourceSystem = "VMI",
        string? saleCode = SaleCode, decimal? totalPremium = 15900m,
        string paymentStatus = "UNPAID", DateTime? paidDate = null) =>
        new("Motor", sourceSystem, "POLICY", documentNo,
            null, null, null, null, null, null, null, null,
            saleCode, null, null, null, null, null, null, null,
            null, null, null,
            null, null, null, totalPremium, null, null, paidDate,
            null, paymentStatus);

    private sealed class LookupGateway(Func<SpDocumentLookupRequest, SpDocumentItem?> answer) : ISpDocumentGateway
    {
        public SpDocumentLookupRequest? Received { get; private set; }

        public Task<SpDocumentSearchResult> SearchAsync(SpDocumentSearchRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SpDocumentItem?> LookupAsync(SpDocumentLookupRequest request, CancellationToken ct)
        {
            Received = request;
            return Task.FromResult(answer(request));
        }
    }

    private static Task<DocumentView?> LookupAsync(ISpDocumentGateway gateway, LookupDocumentQuery query) =>
        new LookupDocumentHandler(gateway, NullLogger<LookupDocumentHandler>.Instance)
            .Handle(query, CancellationToken.None).AsTask();

    private static LookupDocumentQuery Query(string documentNo, ProductGroup group = ProductGroup.VMI) =>
        new(documentNo, group, SaleCode);

    // ------------------------------------------------------------------ handler (REQ-2.5, 3.1, 3.5, 3.6, 3.7)

    // REQ-3.6 — the upstream has no such document: null, which the caller turns into a 400/409.
    [Fact]
    public async Task A_document_the_upstream_does_not_have_is_null()
    {
        var view = await LookupAsync(new LookupGateway(_ => null), Query("69301/กธ/910001"));

        Assert.Null(view);
    }

    // REQ-3.7 — the gateway found more than one exact match and refused to choose. The handler must not swallow it:
    // it is an ArgumentException, so it reaches the client as a 400.
    [Fact]
    public async Task An_ambiguous_lookup_propagates_as_a_rejection()
    {
        var gateway = new LookupGateway(r => throw new SpDocumentAmbiguousException(r.DocumentNo, 2));

        var ex = await Assert.ThrowsAsync<SpDocumentAmbiguousException>(
            () => LookupAsync(gateway, Query("69301/กธ/910001")));

        Assert.IsAssignableFrom<ArgumentException>(ex);
    }

    // REQ-2.6 — the number is trimmed before it travels, so surrounding whitespace still finds the document. The
    // gateway only answers for the trimmed value, so a found result proves the handler trimmed.
    [Fact]
    public async Task Surrounding_whitespace_is_trimmed_before_the_lookup()
    {
        const string exact = "69301/กธ/910001";
        var gateway = new LookupGateway(r => r.DocumentNo == exact ? Row(exact) : null);

        var view = await LookupAsync(gateway, Query($"   {exact}\t"));

        Assert.NotNull(view);
        Assert.Equal(exact, gateway.Received!.DocumentNo);
        Assert.Equal(exact, view!.DocumentNo);
    }

    // REQ-3.5 — every field of the view is the value the upstream returned, including the product group derived
    // from its SourceSystem; the group the caller passed only routed the request.
    [Fact]
    public async Task The_view_takes_its_product_group_from_the_returned_row_not_the_query()
    {
        var gateway = new LookupGateway(_ => Row("26301/POL/000001", sourceSystem: "MISC"));

        var view = await LookupAsync(gateway, Query("26301/POL/000001", group: ProductGroup.CMI));

        Assert.NotNull(view);
        Assert.Equal(ProductGroup.MISC, view!.ProductGroup);
        Assert.Equal(InsuranceType.NonMotor, view.InsuranceType);
    }

    // REQ-3.1 — @SearchText is nvarchar(100); a longer number would bind truncated and look up a different
    // document, so it is refused at the boundary before the gateway is even called.
    [Fact]
    public async Task A_document_number_over_100_characters_is_rejected()
    {
        var gateway = new LookupGateway(_ => throw new InvalidOperationException("must not reach the gateway"));

        await Assert.ThrowsAsync<ArgumentException>(() => LookupAsync(gateway, Query(new string('a', 101))));
        Assert.Null(gateway.Received);
    }

    // REQ-2.5 — blank/whitespace-only or over 150 characters is a 400.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_document_number_is_rejected(string documentNo) =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => LookupAsync(new LookupGateway(_ => null), Query(documentNo)));

    [Fact]
    public async Task A_document_number_over_150_characters_is_rejected() =>
        await Assert.ThrowsAsync<ArgumentException>(
            () => LookupAsync(new LookupGateway(_ => null), Query(new string('a', 151))));

    // ------------------------------------------------------------------ matcher (REQ-2.3, 3.4, 3.7)

    // REQ-3.4 — the LIKE returns prefix hits too; only the row whose DocumentNo equals the wanted one is kept.
    [Fact]
    public void The_matcher_keeps_only_the_exact_document()
    {
        var rows = new[] { Row("69301/กธ/910001"), Row("69301/กธ/9100010"), Row("69301/กธ/910001A") };

        var match = SpDocumentMatch.SelectExactlyOne(rows, "69301/กธ/910001");

        Assert.NotNull(match);
        Assert.Equal("69301/กธ/910001", match!.DocumentNo);
    }

    // REQ-2.3 — same document after trimming both sides and ignoring case (Latin case here; Thai has none).
    [Theory]
    [InlineData("  POL/000001  ")]
    [InlineData("pol/000001")]
    [InlineData("POL/000001")]
    public void The_matcher_compares_trimmed_and_case_insensitively(string stored)
    {
        var match = SpDocumentMatch.SelectExactlyOne([Row(stored)], "POL/000001");

        Assert.NotNull(match);
        Assert.Equal(stored, match!.DocumentNo);
    }

    [Fact]
    public void The_matcher_returns_null_when_nothing_matches() =>
        Assert.Null(SpDocumentMatch.SelectExactlyOne([Row("26301/POL/000002")], "26301/POL/000001"));

    // REQ-3.7 — two rows are the same document: refuse, do not pick one.
    [Fact]
    public void The_matcher_throws_when_more_than_one_row_matches()
    {
        var rows = new[] { Row("69301/กธ/910001", saleCode: "77001"), Row(" 69301/กธ/910001 ", saleCode: "77002") };

        var ex = Assert.Throws<SpDocumentAmbiguousException>(
            () => SpDocumentMatch.SelectExactlyOne(rows, "69301/กธ/910001"));

        Assert.Equal(2, ex.MatchCount);
    }

    // ------------------------------------------------------------------ mapper (fail-closed on an unusable row)

    // A row the upstream returned but that cannot be priced (no premium, blank sale code, or a value outside the
    // wire enums) maps to null, so the handler treats it as not found rather than guessing.
    [Theory]
    [InlineData("VMI", null, "UNPAID")]          // no premium
    [InlineData("ZZZ", 100.0, "UNPAID")]         // unknown source system
    [InlineData("VMI", 100.0, "SETTLED")]        // unknown payment status
    public void An_unusable_row_maps_to_no_view(string sourceSystem, double? premium, string paymentStatus)
    {
        var row = Row("69301/กธ/910001", sourceSystem: sourceSystem,
            totalPremium: premium is null ? null : (decimal)premium.Value, paymentStatus: paymentStatus);

        Assert.Null(SpDocumentItemMapper.ToView(row));
    }

    [Fact]
    public void A_usable_row_maps_to_a_view_carrying_the_upstream_values()
    {
        var view = SpDocumentItemMapper.ToView(
            Row("69301/กธ/910001", sourceSystem: "VMI", totalPremium: 12500m, paymentStatus: "PAID",
                paidDate: new DateTime(2026, 7, 30)));

        Assert.NotNull(view);
        Assert.Equal("69301/กธ/910001", view!.DocumentNo);
        Assert.Equal(ProductGroup.VMI, view.ProductGroup);
        Assert.Equal(12500m, view.TotalPremium);
        Assert.Equal(PaymentStatus.PAID, view.PaymentStatus);
        Assert.Equal(SaleCode, view.SaleCode);
    }
}
