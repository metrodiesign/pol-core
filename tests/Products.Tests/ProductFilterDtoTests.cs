using Products.Application;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// <see cref="ProductFilterDto.Parse"/> — the OPTIONAL filter surface mirroring the §2 input rules of
/// <c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> minus <c>@SaleCode</c>, which is server-side now
/// (products-external-source-of-truth REQ-4.8 — the client cannot choose it). Absent/blank productFilters means
/// "every default"; <c>@PaymentStatus</c> is <c>UNPAID|PAID|ALL</c> case-sensitive with absent = UNPAID (50007),
/// the three From/To pairs reject inversion (50003/50008/50009), and a stray <c>saleCode</c>/<c>branchCode</c>
/// member is ignored, not a 400.
/// </summary>
public sealed class ProductFilterDtoTests
{
    private const string Empty = "{}";

    [Theory]
    [InlineData(null)]                          // absent
    [InlineData("")]                            // blank
    [InlineData("   ")]                         // whitespace
    [InlineData("null")]                        // literal JSON null
    [InlineData("{}")]                          // empty object
    public void Parse_returns_defaults_for_an_absent_or_empty_productFilters(string? raw)
    {
        var dto = ProductFilterDto.Parse(raw);

        Assert.Null(dto.ProductGroup);           // no side chosen — the handler rejects that, not Parse
        Assert.Null(dto.InsuranceType);
        Assert.Equal(PaymentStatus.UNPAID, dto.PaymentStatusFilter);
    }

    // REQ-4.8 — a saleCode member in the client's productFilters is ignored: the server supplies the sale code.
    [Fact]
    public void Parse_ignores_a_client_supplied_saleCode_member()
    {
        var dto = ProductFilterDto.Parse("""{"saleCode":"99999","policyNo":"P-1"}""");

        Assert.Equal("P-1", dto.PolicyNo);
    }

    [Fact]
    public void An_absent_paymentStatus_filters_UNPAID() =>
        Assert.Equal(PaymentStatus.UNPAID, ProductFilterDto.Parse(Empty).PaymentStatusFilter);

    [Fact]
    public void ALL_means_do_not_filter_on_payment_status() =>
        Assert.Null(ProductFilterDto.Parse("""{"paymentStatus":"ALL"}""").PaymentStatusFilter);

    [Theory]
    [InlineData("UNPAID", PaymentStatus.UNPAID)]
    [InlineData("PAID", PaymentStatus.PAID)]
    public void A_wire_payment_status_resolves_to_its_enum(string wire, PaymentStatus expected) =>
        Assert.Equal(expected,
            ProductFilterDto.Parse($$"""{"paymentStatus":"{{wire}}"}""").PaymentStatusFilter);

    [Theory]
    [InlineData("unpaid")]                      // case-sensitive
    [InlineData("NOPE")]
    [InlineData("")]
    [InlineData("0")]                           // Enum.TryParse would read this as UNPAID
    [InlineData("1")]                           // ...and this as PAID
    [InlineData("99")]                          // ...and this as an undefined enum value
    public void Parse_rejects_an_unknown_paymentStatus(string wire) =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse($$"""{"paymentStatus":"{{wire}}"}"""));

    [Fact]
    public void Parse_reads_enums_from_their_uppercase_wire_values()
    {
        var dto = ProductFilterDto.Parse(
            """{"documentType":"POLICY","productGroup":"VMI"}""");

        Assert.Equal(DocumentType.POLICY, dto.DocumentType);
        Assert.Equal(ProductGroup.VMI, dto.ProductGroup);
    }

    [Fact]
    public void Parse_treats_absent_enums_as_ALL()
    {
        var dto = ProductFilterDto.Parse("""{"insuredName":"สมชาย"}""");

        Assert.Null(dto.DocumentType);
        Assert.Null(dto.ProductGroup);
    }

    // REQ-3.2: insuranceType picks which upstream procedure answers. Read like the other enum members here
    // (case-insensitive), unlike paymentStatus/countMode, which are raw wire strings.
    [Theory]
    [InlineData("Motor", InsuranceType.Motor)]
    [InlineData("NonMotor", InsuranceType.NonMotor)]
    [InlineData("motor", InsuranceType.Motor)]
    public void Parse_reads_the_insuranceType(string wire, InsuranceType expected) =>
        Assert.Equal(expected,
            ProductFilterDto.Parse($$"""{"insuranceType":"{{wire}}"}""").InsuranceType);

    [Fact]
    public void An_absent_insuranceType_is_null_and_left_to_the_handler_to_derive() =>
        Assert.Null(ProductFilterDto.Parse(Empty).InsuranceType);

    [Theory]
    [InlineData("Both")]
    [InlineData("0")]                           // a numeric enum token is out of contract
    public void Parse_rejects_an_unknown_insuranceType(string wire) =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse($$"""{"insuranceType":"{{wire}}"}"""));

    // countMode is EXACT|FAST, absent = EXACT, anything else is a 400 at the boundary citing 50006.
    [Fact]
    public void An_absent_countMode_means_EXACT() =>
        Assert.Equal("EXACT", ProductFilterDto.Parse(Empty).CountModeValue);

    [Theory]
    [InlineData("EXACT")]
    [InlineData("FAST")]
    public void A_wire_countMode_is_kept_as_it_is(string wire) =>
        Assert.Equal(wire,
            ProductFilterDto.Parse($$"""{"countMode":"{{wire}}"}""").CountModeValue);

    [Theory]
    [InlineData("fast")]                        // case-sensitive, like the procedure's own comparison
    [InlineData("APPROX")]
    [InlineData("")]
    public void Parse_rejects_an_unknown_countMode(string wire) =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse($$"""{"countMode":"{{wire}}"}"""));

    // @BranchCode is not supported; an unknown JSON member is ignored, not a 400.
    [Fact]
    public void Parse_ignores_a_branchCode_member() =>
        Assert.Equal(ProductGroup.VMI,
            ProductFilterDto.Parse("""{"productGroup":"VMI","branchCode":"001"}""").ProductGroup);

    [Fact]
    public void Parse_rejects_malformed_json() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("{not json"));

    [Fact]
    public void Parse_rejects_an_inverted_PaidDate_range() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse(
            """{"paidDateFrom":"2026-07-02T00:00:00","paidDateTo":"2026-07-01T00:00:00"}"""));

    [Fact]
    public void Parse_rejects_an_inverted_CoverageStart_range() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse(
            """{"coverageStartFrom":"2026-07-02","coverageStartTo":"2026-07-01"}"""));

    [Fact]
    public void Parse_rejects_an_inverted_CoverageEnd_range() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse(
            """{"coverageEndFrom":"2026-07-02","coverageEndTo":"2026-07-01"}"""));

    [Fact]
    public void Parse_accepts_an_equal_From_and_To()
    {
        var dto = ProductFilterDto.Parse(
            """{"coverageStartFrom":"2026-07-01","coverageStartTo":"2026-07-01"}""");

        Assert.Equal(dto.CoverageStartFrom, dto.CoverageStartTo);
    }
}
