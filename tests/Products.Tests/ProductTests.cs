using Products.Domain;

namespace Products.Tests;

/// <summary>
/// Pure domain tests for <see cref="Product.Create"/>'s insurance-document validation
/// (<c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> §2 shared rules) — no DB.
/// </summary>
public sealed class ProductTests
{
    private static ProductInput NewInput(
        ProductGroup productGroup = ProductGroup.VMI,
        DocumentType documentType = DocumentType.POLICY,
        string documentNo = "00098-69100/กธ/037674-10",
        string saleCode = "00098",
        decimal totalPremium = 15900m,
        DateTime? startDate = null,
        DateTime? endDate = null,
        decimal? netPremium = null) =>
        new(productGroup, documentType, documentNo, saleCode, totalPremium,
            StartDate: startDate, EndDate: endDate, NetPremium: netPremium);

    [Fact]
    public void Create_with_valid_document_fields_succeeds()
    {
        var product = Product.Create(NewInput());

        Assert.Equal(ProductGroup.VMI, product.ProductGroup);
        Assert.Equal(DocumentType.POLICY, product.DocumentType);
        Assert.Equal("00098-69100/กธ/037674-10", product.DocumentNo);
        Assert.Equal(15900m, product.TotalPremium);
        Assert.Equal(PaymentStatus.UNPAID, product.PaymentStatus);
        Assert.Null(product.PaidDate);
    }

    [Theory]
    [InlineData(ProductGroup.CMI, InsuranceType.Motor)]
    [InlineData(ProductGroup.VMI, InsuranceType.Motor)]
    [InlineData(ProductGroup.FIRE, InsuranceType.NonMotor)]
    [InlineData(ProductGroup.MISC, InsuranceType.NonMotor)]
    public void InsuranceType_is_derived_from_ProductGroup(ProductGroup group, InsuranceType expected) =>
        Assert.Equal(expected, Product.Create(NewInput(productGroup: group)).InsuranceType);

    [Fact]
    public void Create_rejects_a_blank_SaleCode() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(saleCode: " ")));

    [Fact]
    public void Create_rejects_a_SaleCode_over_20_characters() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(saleCode: new string('9', 21))));

    [Fact]
    public void Create_rejects_a_blank_DocumentNo() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(documentNo: "  ")));

    [Fact]
    public void Create_trims_required_codes()
    {
        var product = Product.Create(NewInput(documentNo: " D-1 ", saleCode: " 00098 "));

        Assert.Equal("D-1", product.DocumentNo);
        Assert.Equal("00098", product.SaleCode);
    }

    [Fact]
    public void Create_normalises_blank_optional_strings_to_null()
    {
        var product = Product.Create(NewInput() with { ShowName = "   ", BrokerName = "" });

        Assert.Null(product.ShowName);
        Assert.Null(product.BrokerName);
    }

    [Fact]
    public void Create_rejects_a_zero_TotalPremium() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(totalPremium: 0m)));

    [Fact]
    public void Create_rejects_a_negative_TotalPremium() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(totalPremium: -1m)));

    // REQ-1.5: shop.Products money columns are decimal(19,2); a third decimal place must be rejected at
    // Create, not silently rounded by SQL Server.
    [Theory]
    [InlineData(100.005)]
    [InlineData(100.001)]
    public void Create_rejects_a_TotalPremium_with_more_than_2_decimal_places(decimal totalPremium) =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(totalPremium: totalPremium)));

    // 100.50m carries a trailing zero the guard must not reject: decimal.Round ignores scale.
    [Fact]
    public void Create_accepts_a_TotalPremium_within_2_decimal_places()
    {
        Assert.Equal(100.5m, Product.Create(NewInput(totalPremium: 100.5m)).TotalPremium);
        Assert.Equal(100.50m, Product.Create(NewInput(totalPremium: 100.50m)).TotalPremium);
        Assert.Equal(100m, Product.Create(NewInput(totalPremium: 100m)).TotalPremium);
    }

    [Fact]
    public void Create_rejects_a_premium_breakdown_with_more_than_2_decimal_places() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(netPremium: 100.005m)));

    [Fact]
    public void Create_rejects_a_negative_premium_breakdown() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(netPremium: -1m)));

    [Fact]
    public void Create_rejects_a_StartDate_after_EndDate() =>
        Assert.Throws<ArgumentException>(() => Product.Create(
            NewInput(startDate: new DateTime(2027, 1, 2), endDate: new DateTime(2027, 1, 1))));

    [Fact]
    public void Create_accepts_an_equal_StartDate_and_EndDate()
    {
        var day = new DateTime(2027, 1, 1);
        var product = Product.Create(NewInput(startDate: day, endDate: day));

        Assert.Equal(day, product.StartDate);
        Assert.Equal(day, product.EndDate);
    }

    [Fact]
    public void Create_rejects_a_CMI_APPLICATION_document() =>
        Assert.Throws<ArgumentException>(() => Product.Create(
            NewInput(productGroup: ProductGroup.CMI, documentType: DocumentType.APPLICATION)));

    [Fact]
    public void Create_rejects_an_undefined_ProductGroup() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(productGroup: (ProductGroup)99)));

    [Fact]
    public void Create_rejects_an_undefined_DocumentType() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(documentType: (DocumentType)99)));

    [Fact]
    public void Create_allows_a_MISC_APPLICATION_document() =>
        Assert.Equal(DocumentType.APPLICATION, Product.Create(
            NewInput(productGroup: ProductGroup.MISC, documentType: DocumentType.APPLICATION)).DocumentType);

    [Fact]
    public void Premium_breakdown_defaults_to_null()
    {
        var product = Product.Create(NewInput(netPremium: 14800m));

        Assert.Equal(14800m, product.NetPremium);
        Assert.Null(product.Stamp);
        Assert.Null(product.TaxVat);
        Assert.Null(product.CommissionAmount);
    }

    [Fact]
    public void MarkPaid_sets_status_and_paid_date()
    {
        var product = Product.Create(NewInput());
        var paidAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        product.MarkPaid(paidAt);

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Equal(paidAt, product.PaidDate);
    }
}
