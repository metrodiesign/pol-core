using Products.Domain;

namespace Products.Tests;

/// <summary>
/// Pure domain tests for the insurance-document validation shared by <see cref="Product.Create"/> and
/// <see cref="Product.RefreshFromExternal"/> (<c>docs/reference/vcentralpay-sp-quick-reference.pdf</c> §2
/// shared rules) — no DB.
/// </summary>
public sealed class ProductTests
{
    private static ProductInput NewInput(
        ProductGroup productGroup = ProductGroup.VMI,
        DocumentType documentType = DocumentType.POLICY,
        string documentNo = "00098-69100/กธ/037674-10",
        string saleCode = "00098",
        decimal totalPremium = 15900m,
        PaymentStatus paymentStatus = PaymentStatus.UNPAID,
        DateTime? paidDate = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        decimal? netPremium = null) =>
        new(productGroup, documentType, documentNo, saleCode, totalPremium, paymentStatus, paidDate,
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

    // REQ-7.2 (B1): a row the upstream already reports as PAID must not be born UNPAID, or the cart gate
    // would let it be sold again.
    [Fact]
    public void Create_honours_a_PAID_row_from_upstream()
    {
        var paidAt = new DateTime(2026, 7, 20, 9, 30, 0);

        var product = Product.Create(NewInput(paymentStatus: PaymentStatus.PAID, paidDate: paidAt));

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Equal(paidAt, product.PaidDate);
    }

    // REQ-7.6: the upstream does not always send a PaidDate with a PAID row — the status still stands.
    [Fact]
    public void Create_accepts_a_PAID_row_without_a_PaidDate()
    {
        var product = Product.Create(NewInput(paymentStatus: PaymentStatus.PAID));

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Null(product.PaidDate);
    }

    [Fact]
    public void Create_drops_a_PaidDate_that_comes_with_an_UNPAID_row()
    {
        var product = Product.Create(
            NewInput(paymentStatus: PaymentStatus.UNPAID, paidDate: new DateTime(2026, 7, 20)));

        Assert.Null(product.PaidDate);
    }

    [Fact]
    public void Create_rejects_an_undefined_PaymentStatus() =>
        Assert.Throws<ArgumentException>(() => Product.Create(NewInput(paymentStatus: (PaymentStatus)99)));

    [Fact]
    public void RefreshFromExternal_updates_the_document_fields()
    {
        var product = Product.Create(NewInput());

        product.RefreshFromExternal(NewInput(totalPremium: 17250m, netPremium: 16000m) with
        {
            ShowName = " สมชาย ใจดี ",
            PolicyNumber = "00098-69100/037674",
        });

        Assert.Equal(17250m, product.TotalPremium);
        Assert.Equal(16000m, product.NetPremium);
        Assert.Equal("สมชาย ใจดี", product.ShowName);
        Assert.Equal("00098-69100/037674", product.PolicyNumber);
    }

    // REQ-7.4: the upstream is eventually consistent with our own order flow, so a wire UNPAID must never
    // undo a payment we recorded — while the rest of the row still refreshes.
    [Fact]
    public void RefreshFromExternal_never_downgrades_a_local_PAID()
    {
        var paidAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var product = Product.Create(NewInput());
        product.MarkPaid(paidAt);

        product.RefreshFromExternal(NewInput(paymentStatus: PaymentStatus.UNPAID, totalPremium: 17250m));

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Equal(paidAt, product.PaidDate);
        Assert.Equal(17250m, product.TotalPremium);
    }

    // REQ-7.5
    [Fact]
    public void RefreshFromExternal_takes_the_wire_PAID_and_its_PaidDate()
    {
        var product = Product.Create(NewInput());
        var paidAt = new DateTime(2026, 7, 25, 8, 0, 0);

        product.RefreshFromExternal(NewInput(paymentStatus: PaymentStatus.PAID, paidDate: paidAt));

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Equal(paidAt, product.PaidDate);
    }

    // REQ-7.6: PAID with no date keeps whatever date we already had, rather than blanking it.
    [Fact]
    public void RefreshFromExternal_keeps_the_existing_PaidDate_when_the_wire_sends_none()
    {
        var paidAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var product = Product.Create(NewInput());
        product.MarkPaid(paidAt);

        product.RefreshFromExternal(NewInput(paymentStatus: PaymentStatus.PAID, paidDate: null));

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Equal(paidAt, product.PaidDate);
    }

    // REQ-7.3: the caller looked the row up by DocumentNo, so a different one means it matched the wrong
    // aggregate — refusing beats overwriting one document with another's data.
    [Fact]
    public void RefreshFromExternal_rejects_a_different_DocumentNo() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput()).RefreshFromExternal(NewInput(documentNo: "00098-69100/กธ/999999-10")));

    // IX_Products_DocumentNo is unique under a case-insensitive collation: a row the database matched by
    // a case-variant DocumentNo is still this document, so it must refresh — and adopt the wire casing,
    // the same way every other field mirrors the upstream.
    [Fact]
    public void RefreshFromExternal_matches_the_DocumentNo_case_insensitively()
    {
        var product = Product.Create(NewInput(documentNo: "D-CASE-1"));

        product.RefreshFromExternal(NewInput(documentNo: "d-case-1", totalPremium: 500m));

        Assert.Equal("d-case-1", product.DocumentNo);
        Assert.Equal(500m, product.TotalPremium);
    }

    [Fact]
    public void RefreshFromExternal_matches_the_DocumentNo_after_trimming()
    {
        var product = Product.Create(NewInput(documentNo: " D-1 "));

        product.RefreshFromExternal(NewInput(documentNo: "  D-1  ", totalPremium: 500m));

        Assert.Equal(500m, product.TotalPremium);
    }

    // REQ-7.3 (M9): CMI/VMI and FIRE/MISC live in different source systems entirely.
    [Fact]
    public void RefreshFromExternal_rejects_a_ProductGroup_from_the_other_InsuranceType_side() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput(productGroup: ProductGroup.VMI))
                .RefreshFromExternal(NewInput(productGroup: ProductGroup.FIRE)));

    [Theory]
    [InlineData(ProductGroup.VMI, ProductGroup.CMI)]
    [InlineData(ProductGroup.CMI, ProductGroup.VMI)]
    [InlineData(ProductGroup.FIRE, ProductGroup.MISC)]
    [InlineData(ProductGroup.MISC, ProductGroup.FIRE)]
    public void RefreshFromExternal_allows_a_ProductGroup_within_the_same_side(
        ProductGroup local, ProductGroup wire)
    {
        var product = Product.Create(NewInput(productGroup: local));

        product.RefreshFromExternal(NewInput(productGroup: wire));

        Assert.Equal(wire, product.ProductGroup);
    }

    // REQ-7.3: one apply-fields path means every Create rule bites on a refresh too.
    [Fact]
    public void RefreshFromExternal_rejects_a_premium_with_more_than_2_decimal_places() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput()).RefreshFromExternal(NewInput(netPremium: 100.005m)));

    [Fact]
    public void RefreshFromExternal_rejects_a_zero_TotalPremium() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput()).RefreshFromExternal(NewInput(totalPremium: 0m)));

    [Fact]
    public void RefreshFromExternal_rejects_a_CMI_APPLICATION_document() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput(productGroup: ProductGroup.CMI))
                .RefreshFromExternal(
                    NewInput(productGroup: ProductGroup.CMI, documentType: DocumentType.APPLICATION)));

    [Fact]
    public void RefreshFromExternal_rejects_a_StartDate_after_EndDate() =>
        Assert.Throws<ArgumentException>(() =>
            Product.Create(NewInput()).RefreshFromExternal(
                NewInput(startDate: new DateTime(2027, 1, 2), endDate: new DateTime(2027, 1, 1))));
}
