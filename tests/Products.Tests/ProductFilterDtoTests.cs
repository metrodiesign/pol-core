using Products.Application;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// <see cref="ProductFilterDto.Parse"/> — the mandatory filter surface mirroring the §2 input rules of
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c>: <c>@SaleCode</c> is required (SP error 50005),
/// <c>@PaymentStatus</c> is <c>UNPAID|PAID|ALL</c> case-sensitive with absent = UNPAID (50007), the three
/// From/To pairs reject inversion (50003/50008/50009), and <c>@BranchCode</c> is not supported.
/// </summary>
public sealed class ProductFilterDtoTests
{
    private const string SaleCodeOnly = """{"saleCode":"00098"}""";

    [Theory]
    [InlineData(null)]                          // absent
    [InlineData("")]                            // blank
    [InlineData("   ")]                         // whitespace
    [InlineData("null")]                        // literal JSON null
    public void Parse_rejects_a_missing_productFilters(string? raw) =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse(raw));

    [Fact]
    public void Parse_rejects_a_missing_SaleCode() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("""{"policyNo":"P-1"}"""));

    [Fact]
    public void Parse_rejects_a_blank_SaleCode() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("""{"saleCode":"   "}"""));

    [Fact]
    public void Parse_trims_the_SaleCode() =>
        Assert.Equal("S1", ProductFilterDto.Parse("""{"saleCode":"  S1  "}""").SaleCode);

    [Fact]
    public void Parse_rejects_a_SaleCode_over_20_characters() =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse($$"""{"saleCode":"{{new string('9', 21)}}"}"""));

    [Fact]
    public void Parse_accepts_a_SaleCode_of_exactly_20_characters() =>
        Assert.Equal(new string('9', 20),
            ProductFilterDto.Parse($$"""{"saleCode":"{{new string('9', 20)}}"}""").SaleCode);

    [Fact]
    public void An_absent_paymentStatus_filters_UNPAID() =>
        Assert.Equal(PaymentStatus.UNPAID, ProductFilterDto.Parse(SaleCodeOnly).PaymentStatusFilter);

    [Fact]
    public void ALL_means_do_not_filter_on_payment_status() =>
        Assert.Null(ProductFilterDto.Parse("""{"saleCode":"00098","paymentStatus":"ALL"}""").PaymentStatusFilter);

    [Theory]
    [InlineData("UNPAID", PaymentStatus.UNPAID)]
    [InlineData("PAID", PaymentStatus.PAID)]
    public void A_wire_payment_status_resolves_to_its_enum(string wire, PaymentStatus expected) =>
        Assert.Equal(expected,
            ProductFilterDto.Parse($$"""{"saleCode":"00098","paymentStatus":"{{wire}}"}""").PaymentStatusFilter);

    [Theory]
    [InlineData("unpaid")]                      // case-sensitive
    [InlineData("NOPE")]
    [InlineData("")]
    public void Parse_rejects_an_unknown_paymentStatus(string wire) =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse($$"""{"saleCode":"00098","paymentStatus":"{{wire}}"}"""));

    [Fact]
    public void Parse_reads_enums_from_their_uppercase_wire_values()
    {
        var dto = ProductFilterDto.Parse(
            """{"saleCode":"00098","documentType":"POLICY","productGroup":"VMI"}""");

        Assert.Equal(DocumentType.POLICY, dto.DocumentType);
        Assert.Equal(ProductGroup.VMI, dto.ProductGroup);
    }

    [Fact]
    public void Parse_treats_absent_enums_as_ALL()
    {
        var dto = ProductFilterDto.Parse("""{"saleCode":"00098","insuredName":"สมชาย"}""");

        Assert.Null(dto.DocumentType);
        Assert.Null(dto.ProductGroup);
    }

    // REQ-3.4: @BranchCode is not supported; an unknown JSON member is ignored, not a 400.
    [Fact]
    public void Parse_ignores_a_branchCode_member() =>
        Assert.Equal("00098", ProductFilterDto.Parse("""{"saleCode":"00098","branchCode":"001"}""").SaleCode);

    [Fact]
    public void Parse_rejects_malformed_json() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("{not json"));

    [Fact]
    public void Parse_rejects_an_inverted_PaidDate_range() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse(
            """{"saleCode":"00098","paidDateFrom":"2026-07-02T00:00:00","paidDateTo":"2026-07-01T00:00:00"}"""));

    [Fact]
    public void Parse_rejects_an_inverted_CoverageStart_range() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse(
            """{"saleCode":"00098","coverageStartFrom":"2026-07-02","coverageStartTo":"2026-07-01"}"""));

    [Fact]
    public void Parse_rejects_an_inverted_CoverageEnd_range() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse(
            """{"saleCode":"00098","coverageEndFrom":"2026-07-02","coverageEndTo":"2026-07-01"}"""));

    [Fact]
    public void Parse_accepts_an_equal_From_and_To()
    {
        var dto = ProductFilterDto.Parse(
            """{"saleCode":"00098","coverageStartFrom":"2026-07-01","coverageStartTo":"2026-07-01"}""");

        Assert.Equal(dto.CoverageStartFrom, dto.CoverageStartTo);
    }
}
