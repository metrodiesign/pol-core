using Products.Domain;
using SharedKernel;

namespace Products.Tests;

/// <summary>
/// Pure domain tests for <see cref="Product.Create"/>'s insurance-document validation (VCentralPay SP
/// guide §2 shared rules) — no DB.
/// </summary>
public sealed class ProductTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTime At = new(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc);

    private static ProductInput NewInput(
        ProductGroup productGroup = ProductGroup.VMI,
        DocumentType documentType = DocumentType.POLICY,
        string documentNo = "00098-69100/กธ/037674-10",
        string branchCode = "100",
        string saleCode = "00098",
        decimal totalPremium = 15900m,
        string totalPremiumCurrency = "THB",
        DateTime? startDate = null,
        DateTime? endDate = null,
        Money? netPremium = null) =>
        new(MerchantId, productGroup, documentType, documentNo, branchCode, saleCode,
            Money.Of(totalPremium, totalPremiumCurrency),
            StartDate: startDate, EndDate: endDate, NetPremium: netPremium);

    [Fact]
    public void Create_with_valid_document_fields_succeeds()
    {
        var product = Product.Create(NewInput(), At);

        Assert.Equal(ProductGroup.VMI, product.ProductGroup);
        Assert.Equal(DocumentType.POLICY, product.DocumentType);
        Assert.Equal("00098-69100/กธ/037674-10", product.DocumentNo);
        Assert.Equal(Money.Of(15900m, "THB"), product.TotalPremium);
        Assert.Equal(PaymentStatus.UNPAID, product.PaymentStatus);
        Assert.Null(product.PaidDate);
        Assert.True(product.IsActive);
        Assert.Equal(At, product.CreatedAt);
    }

    [Theory]
    [InlineData(ProductGroup.CMI, InsuranceType.Motor)]
    [InlineData(ProductGroup.VMI, InsuranceType.Motor)]
    [InlineData(ProductGroup.FIRE, InsuranceType.NonMotor)]
    [InlineData(ProductGroup.MISC, InsuranceType.NonMotor)]
    public void InsuranceType_is_derived_from_ProductGroup(ProductGroup group, InsuranceType expected) =>
        Assert.Equal(expected, Product.Create(NewInput(productGroup: group), At).InsuranceType);

    [Fact]
    public void Create_rejects_an_empty_MerchantId() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput() with { MerchantId = Guid.Empty }, At));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_BranchCode(string branchCode) =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(branchCode: branchCode), At));

    [Fact]
    public void Create_rejects_a_BranchCode_over_3_characters() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(branchCode: "1000"), At));

    [Fact]
    public void Create_rejects_a_blank_SaleCode() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(saleCode: " "), At));

    [Fact]
    public void Create_rejects_a_blank_DocumentNo() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(documentNo: "  "), At));

    [Fact]
    public void Create_trims_required_codes()
    {
        var product = Product.Create(NewInput(branchCode: " 100 ", saleCode: " 00098 "), At);

        Assert.Equal("100", product.BranchCode);
        Assert.Equal("00098", product.SaleCode);
    }

    [Fact]
    public void Create_normalises_blank_optional_strings_to_null()
    {
        var product = Product.Create(NewInput() with { ShowName = "   ", BrokerName = "" }, At);

        Assert.Null(product.ShowName);
        Assert.Null(product.BrokerName);
    }

    [Fact]
    public void Create_rejects_a_zero_TotalPremium() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(totalPremium: 0m), At));

    [Fact]
    public void Create_rejects_a_non_THB_TotalPremium() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(totalPremiumCurrency: "USD"), At));

    [Fact]
    public void Create_rejects_a_non_THB_premium_breakdown() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput(netPremium: Money.Of(100m, "USD")), At));

    [Fact]
    public void Create_rejects_a_StartDate_after_EndDate() =>
        Assert.Throws<ArgumentException>(() => Product.Create(
            NewInput(startDate: new DateTime(2027, 1, 2), endDate: new DateTime(2027, 1, 1)), At));

    [Fact]
    public void Create_accepts_an_equal_StartDate_and_EndDate()
    {
        var day = new DateTime(2027, 1, 1);
        var product = Product.Create(NewInput(startDate: day, endDate: day), At);

        Assert.Equal(day, product.StartDate);
        Assert.Equal(day, product.EndDate);
    }

    [Fact]
    public void Create_rejects_a_CMI_APPLICATION_document() =>
        Assert.Throws<ArgumentException>(() => Product.Create(
            NewInput(productGroup: ProductGroup.CMI, documentType: DocumentType.APPLICATION), At));

    [Fact]
    public void Create_allows_a_MISC_APPLICATION_document() =>
        Assert.Equal(DocumentType.APPLICATION, Product.Create(
            NewInput(productGroup: ProductGroup.MISC, documentType: DocumentType.APPLICATION), At).DocumentType);

    [Fact]
    public void Premium_breakdown_pairs_round_trip_as_Money()
    {
        var product = Product.Create(NewInput(netPremium: Money.Of(14800m, "THB")), At);

        Assert.Equal(Money.Of(14800m, "THB"), product.NetPremium);
        Assert.Null(product.Stamp);
        Assert.Null(product.TaxVat);
        Assert.Null(product.CommissionAmount);
    }

    [Fact]
    public void MarkPaid_sets_status_and_leaves_the_sellable_catalog()
    {
        var product = Product.Create(NewInput(), At);
        var paidAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

        product.MarkPaid(paidAt);

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Equal(paidAt, product.PaidDate);
        Assert.False(product.IsActive);
    }

    [Fact]
    public void Deactivate_removes_the_document_from_the_sellable_catalog()
    {
        var product = Product.Create(NewInput(), At);

        product.Deactivate();

        Assert.False(product.IsActive);
    }
}
