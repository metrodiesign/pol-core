using Products.Application;
using Products.Application.Ports;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// Unit tests for <see cref="SpDocumentItemMapper"/> — the §5.2 row to <see cref="DocumentView"/>
/// translation (products-external-source-of-truth REQ-1.6, REQ-1.7, REQ-3.5). No DB, no gateway.
/// </summary>
public sealed class SpDocumentItemMapperTests
{
    private const string DocumentNo = "77001-69900/กธ/950001-10";

    /// <summary>A row that maps cleanly, so each test can spoil exactly one thing.</summary>
    private static SpDocumentItem Row(
        string? documentNo = DocumentNo,
        string? saleCode = "77001",
        string? sourceSystem = "VMI",
        string? documentType = "POLICY",
        string? paymentStatus = "UNPAID",
        decimal? totalPremium = 15900m,
        DateTime? paidDate = null,
        string? insuranceType = "Motor") =>
        new(insuranceType, sourceSystem, documentType, documentNo,
            null, null, null, null, null, null, null, null,
            saleCode, null, null, null, null, null, null, null,
            null, null, null,
            null, null, null, totalPremium, null, null, paidDate,
            null, paymentStatus);

    private static MappedSpDocument MapOne(SpDocumentItem item) =>
        Assert.Single(SpDocumentItemMapper.Map([item]));

    [Fact]
    public void Every_field_of_a_row_reaches_the_ProductInput()
    {
        var start = new DateTime(2026, 7, 1, 8, 30, 0);
        var end = new DateTime(2027, 6, 30, 23, 59, 59);
        var paidAt = new DateTime(2026, 7, 20, 9, 15, 0);

        var input = MapOne(new SpDocumentItem(
            "NonMotor", "VMI", "RENEWAL", $" {DocumentNo} ",
            "69", "900", "pre", "seq-1", "68", "ref-1", "branch-1", "type-1",
            " 77001 ", "สมชาย ขาย", "BK", "โบรกเกอร์",
            "POL-1", "APP-1", "POL-0", "E950001",
            start, end, "ผู้เอาประกัน",
            14000m, 10m, 980m, 15900m, 12.5m, 500m, paidAt,
            "9ฮฮ 9999", "PAID")).View;

        Assert.NotNull(input);
        // REQ-7.10: the group comes from SourceSystem and the row's own InsuranceType ("NonMotor" here,
        // deliberately contradicting it) is ignored — this side is always derived from the group.
        Assert.Equal(ProductGroup.VMI, input.ProductGroup);
        Assert.Equal(DocumentType.RENEWAL, input.DocumentType);
        Assert.Equal(DocumentNo, input.DocumentNo);
        Assert.Equal("77001", input.SaleCode);
        Assert.Equal(15900m, input.TotalPremium);
        Assert.Equal(PaymentStatus.PAID, input.PaymentStatus);
        Assert.Equal(paidAt, input.PaidDate);
        Assert.Equal("69", input.PolicyYear);
        Assert.Equal("900", input.ReferenceBranch);
        Assert.Equal("pre", input.ReferencePre);
        Assert.Equal("seq-1", input.PolicySequenceNo);
        Assert.Equal("68", input.ReferenceYear);
        Assert.Equal("ref-1", input.ReferenceNo);
        Assert.Equal("branch-1", input.PolicyBranch);
        Assert.Equal("type-1", input.PolicyType);
        Assert.Equal("สมชาย ขาย", input.SaleFullName);
        Assert.Equal("BK", input.BrokerCode);
        Assert.Equal("โบรกเกอร์", input.BrokerName);
        Assert.Equal("POL-1", input.PolicyNumber);
        Assert.Equal("APP-1", input.ApplicationNumber);
        Assert.Equal("POL-0", input.PreviousPolicyNumber);
        Assert.Equal("E950001", input.EndorsementNumber);
        Assert.Equal(start, input.StartDate);
        Assert.Equal(end, input.EndDate);
        Assert.Equal("ผู้เอาประกัน", input.ShowName);
        Assert.Equal("9ฮฮ 9999", input.LicensePlateNumber);
        Assert.Equal(14000m, input.NetPremium);
        Assert.Equal(10m, input.Stamp);
        Assert.Equal(980m, input.TaxVat);
        // The two commission fields sit in the opposite order on the two records — a swap here would be
        // a silent 500 THB commission rate.
        Assert.Equal(500m, input.CommissionAmount);
        Assert.Equal(12.5m, input.CommissionPercent);
    }

    [Fact]
    public void A_mapped_row_carries_no_skip_reason()
    {
        var mapped = MapOne(Row());

        Assert.NotNull(mapped.View);
        Assert.Null(mapped.SkipReason);
    }

    // The SP contract is datetime2(0) with no offset (Thailand local time) — no conversion anywhere on
    // the path, so the Kind stays Unspecified.
    [Fact]
    public void Dates_pass_through_naive()
    {
        var paidAt = new DateTime(2026, 7, 20, 9, 15, 0, DateTimeKind.Unspecified);

        var input = MapOne(Row(paymentStatus: "PAID", paidDate: paidAt)).View;

        Assert.Equal(paidAt, input!.PaidDate);
        Assert.Equal(DateTimeKind.Unspecified, input.PaidDate!.Value.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_row_without_a_DocumentNo_is_skipped(string? documentNo)
    {
        var mapped = MapOne(Row(documentNo: documentNo));

        Assert.Null(mapped.View);
        Assert.Contains("DocumentNo", mapped.SkipReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_row_without_a_SaleCode_is_skipped(string? saleCode)
    {
        var mapped = MapOne(Row(saleCode: saleCode));

        Assert.Null(mapped.View);
        Assert.Contains("SaleCode", mapped.SkipReason);
    }

    // double, not decimal: xunit cannot hand an InlineData literal to a decimal? parameter.
    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void A_row_without_a_sellable_TotalPremium_is_skipped(double? totalPremium)
    {
        var mapped = MapOne(Row(totalPremium: (decimal?)totalPremium));

        Assert.Null(mapped.View);
        Assert.Contains("TotalPremium", mapped.SkipReason);
    }

    // Case-sensitive on purpose: the wire values are uppercase (§2), and a lowercase "vmi" is an upstream
    // change worth seeing in the log, not a value to guess at.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MOTOR")]
    [InlineData("vmi")]
    [InlineData("0")]
    [InlineData("CMI, VMI")]
    public void A_row_with_an_unknown_SourceSystem_is_skipped(string? sourceSystem)
    {
        var mapped = MapOne(Row(sourceSystem: sourceSystem));

        Assert.Null(mapped.View);
        Assert.Contains("SourceSystem", mapped.SkipReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("policy")]
    [InlineData("ALL")]
    [InlineData("1")]
    public void A_row_with_an_unknown_DocumentType_is_skipped(string? documentType)
    {
        var mapped = MapOne(Row(documentType: documentType));

        Assert.Null(mapped.View);
        Assert.Contains("DocumentType", mapped.SkipReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("paid")]
    [InlineData("ALL")]
    [InlineData("0")]
    public void A_row_with_an_unknown_PaymentStatus_is_skipped(string? paymentStatus)
    {
        var mapped = MapOne(Row(paymentStatus: paymentStatus));

        Assert.Null(mapped.View);
        Assert.Contains("PaymentStatus", mapped.SkipReason);
    }

    // MINOR-11: two rows for one document would be two Adds against IX_Products_DocumentNo in one save.
    [Fact]
    public void A_duplicate_DocumentNo_within_the_page_keeps_the_first_row()
    {
        var mapped = SpDocumentItemMapper.Map(
            [Row(totalPremium: 100m), Row(totalPremium: 200m), Row(documentNo: "77001-69900/กธ/950002-10")]);

        Assert.Equal(3, mapped.Count);
        Assert.Equal(100m, mapped[0].View!.TotalPremium);
        Assert.Null(mapped[1].View);
        Assert.Contains("duplicate", mapped[1].SkipReason);
        Assert.Equal("77001-69900/กธ/950002-10", mapped[2].View!.DocumentNo);
    }

    // The claim is case-insensitive because IX_Products_DocumentNo is: "ab" after "AB" is the same
    // document, and letting it through would be the same two Adds against the index.
    [Fact]
    public void A_case_variant_DocumentNo_within_the_page_is_a_duplicate()
    {
        var mapped = SpDocumentItemMapper.Map(
            [Row(documentNo: "77001-69900/AB/950009-10", totalPremium: 100m),
             Row(documentNo: "77001-69900/ab/950009-10", totalPremium: 200m)]);

        Assert.Equal(100m, mapped[0].View!.TotalPremium);
        Assert.Null(mapped[1].View);
        Assert.Contains("duplicate", mapped[1].SkipReason);
    }

    [Fact]
    public void A_skipped_row_does_not_claim_its_DocumentNo()
    {
        var mapped = SpDocumentItemMapper.Map([Row(totalPremium: 0m), Row(totalPremium: 200m)]);

        Assert.Null(mapped[0].View);
        Assert.Equal(200m, mapped[1].View!.TotalPremium);
    }

    [Fact]
    public void Rows_keep_the_order_the_procedure_returned_them_in()
    {
        var mapped = SpDocumentItemMapper.Map(
            [Row(documentNo: "b"), Row(saleCode: " "), Row(documentNo: "a")]);

        Assert.Equal("b", mapped[0].View!.DocumentNo);
        Assert.Null(mapped[1].View);
        Assert.Equal("a", mapped[2].View!.DocumentNo);
    }

    [Fact]
    public void An_empty_page_maps_to_an_empty_list() =>
        Assert.Empty(SpDocumentItemMapper.Map([]));
}
