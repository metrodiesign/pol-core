using Products.Application;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// <see cref="ProductFilterDto.Parse"/> — the typed filter surface mirroring the VCentralPay SP guide §2
/// shared rules: null enum = ALL, and the three From/To pairs reject inversion (SP errors 50003/50008/50009).
/// </summary>
public sealed class ProductFilterDtoTests
{
    [Fact]
    public void Parse_returns_null_for_an_absent_value() =>
        Assert.Null(ProductFilterDto.Parse(null));

    [Fact]
    public void Parse_reads_enums_from_their_uppercase_wire_values()
    {
        var dto = ProductFilterDto.Parse("""{"paymentStatus":"UNPAID","documentType":"POLICY","productGroup":"VMI"}""");

        Assert.NotNull(dto);
        Assert.Equal(PaymentStatus.UNPAID, dto!.PaymentStatus);
        Assert.Equal(DocumentType.POLICY, dto.DocumentType);
        Assert.Equal(ProductGroup.VMI, dto.ProductGroup);
    }

    [Fact]
    public void Parse_treats_absent_enums_as_ALL()
    {
        var dto = ProductFilterDto.Parse("""{"insuredName":"สมชาย"}""");

        Assert.NotNull(dto);
        Assert.Null(dto!.PaymentStatus);
        Assert.Null(dto.DocumentType);
        Assert.Null(dto.ProductGroup);
    }

    [Fact]
    public void Parse_rejects_malformed_json() =>
        Assert.Throws<ArgumentException>(() => ProductFilterDto.Parse("{not json"));

    [Fact]
    public void Parse_rejects_an_inverted_PaidDate_range() =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse("""{"paidDateFrom":"2026-07-02T00:00:00","paidDateTo":"2026-07-01T00:00:00"}"""));

    [Fact]
    public void Parse_rejects_an_inverted_CoverageStart_range() =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse("""{"coverageStartFrom":"2026-07-02","coverageStartTo":"2026-07-01"}"""));

    [Fact]
    public void Parse_rejects_an_inverted_CoverageEnd_range() =>
        Assert.Throws<ArgumentException>(() =>
            ProductFilterDto.Parse("""{"coverageEndFrom":"2026-07-02","coverageEndTo":"2026-07-01"}"""));

    [Fact]
    public void Parse_accepts_an_equal_From_and_To()
    {
        var dto = ProductFilterDto.Parse("""{"coverageStartFrom":"2026-07-01","coverageStartTo":"2026-07-01"}""");

        Assert.NotNull(dto);
        Assert.Equal(dto!.CoverageStartFrom, dto.CoverageStartTo);
    }
}
