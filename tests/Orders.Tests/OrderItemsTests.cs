using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

/// <summary>
/// Pure domain tests for <see cref="Order.Create"/>'s line handling (insurance-pivot REQ-6/7) — no DB.
/// Covers the line-sum invariant (6.3), empty-lines rejection (6.7), the quantity==1 constraint, and the
/// insured-person validation on <see cref="Orders.Domain.Items.Item"/> (7.2), including that the thrown
/// exception never echoes the invalid PII value (7.3).
/// </summary>
public sealed class OrderItemsTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly Guid ProductB = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Default-param helper so a signature change touches one line, not every call site.
    private static OrderItemInput Item(
        Guid productId, decimal unitPrice, int quantity = 1, string idNumber = "1234567890123",
        string currency = "THB", DateTime? dob = null,
        string documentNo = "00098-69100/กธ/900001-10", string productGroup = "VMI", string documentType = "POLICY",
        string? policyNumber = null, DateTime? startDate = null, DateTime? endDate = null) =>
        new(productId, quantity, Money.Of(unitPrice, currency),
            documentNo, productGroup, documentType, policyNumber, startDate, endDate,
            "Somchai", "Jaidee", idNumber, dob ?? Dob);

    [Fact]
    public void Create_with_one_line_matching_the_amount_succeeds()
    {
        var order = Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Item(ProductA, 15000m)]);

        var item = Assert.Single(order.Items);
        Assert.Equal(ProductA, item.ProductId);
        Assert.Equal(order.Id, item.OrderId);
        Assert.Equal(MerchantId, item.MerchantId);
    }

    [Fact]
    public void Create_with_multiple_lines_summing_to_the_amount_succeeds()
    {
        var order = Order.Create(
            MerchantId, Money.Of(25000m, "THB"), At, [Item(ProductA, 15000m), Item(ProductB, 10000m)]);

        Assert.Equal(2, order.Items.Count);
    }

    [Fact]
    public void Create_rejects_an_empty_line_list()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, []));
        Assert.Equal("items", ex.ParamName);
    }

    [Fact]
    public void Create_rejects_a_line_whose_quantity_is_not_1()
    {
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(30000m, "THB"), At, [Item(ProductA, 15000m, quantity: 2)]));
    }

    [Fact]
    public void Create_rejects_a_line_sum_that_does_not_match_the_amount()
    {
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Item(ProductA, 14999m)]));
    }

    [Fact]
    public void Create_rejects_a_line_currency_mismatched_with_the_amount()
    {
        var mismatched = Item(ProductA, 15000m, currency: "USD");

        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [mismatched]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_insured_IdNumber(string idNumber) =>
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Item(ProductA, 15000m, idNumber: idNumber)]));

    [Fact]
    public void Create_rejects_a_future_date_of_birth()
    {
        var futureDob = Item(ProductA, 15000m, dob: At.AddDays(1));

        Assert.Throws<ArgumentException>(() => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [futureDob]));
    }

    [Fact]
    public void The_thrown_exception_never_echoes_the_invalid_date_of_birth_value()
    {
        var distinctiveFutureDob = new DateTime(2099, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var bad = Item(ProductA, 15000m, dob: distinctiveFutureDob);

        var ex = Assert.Throws<ArgumentException>(() => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [bad]));

        Assert.DoesNotContain("2099", ex.Message, StringComparison.Ordinal);
    }

    // checkout-chain-document-fields REQ-1.4 — defense in depth: the same document invariants Checkouts.Item
    // enforces at checkout-start are re-checked here, at Order.Create.
    [Theory]
    [InlineData("", "VMI", "POLICY")]
    [InlineData("   ", "VMI", "POLICY")]
    [InlineData("DOC-1", "", "POLICY")]
    [InlineData("DOC-1", "VMI", "  ")]
    public void Create_rejects_a_blank_document_field(string documentNo, string productGroup, string documentType) =>
        Assert.Throws<ArgumentException>(() => Order.Create(
            MerchantId, Money.Of(15000m, "THB"), At,
            [Item(ProductA, 15000m, documentNo: documentNo, productGroup: productGroup, documentType: documentType)]));

    [Fact]
    public void Create_rejects_a_start_date_after_the_end_date()
    {
        var ex = Assert.Throws<ArgumentException>(() => Order.Create(
            MerchantId, Money.Of(15000m, "THB"), At,
            [Item(ProductA, 15000m, startDate: At.AddDays(10), endDate: At)]));

        Assert.Equal("startDate", ex.ParamName);
    }

    [Fact]
    public void Document_fields_are_trimmed_onto_the_line()
    {
        var order = Order.Create(
            MerchantId, Money.Of(15000m, "THB"), At,
            [Item(ProductA, 15000m, documentNo: "  DOC-1  ", productGroup: " VMI ", documentType: " POLICY ")]);

        var item = Assert.Single(order.Items);
        Assert.Equal("DOC-1", item.DocumentNo);
        Assert.Equal("VMI", item.ProductGroup);
        Assert.Equal("POLICY", item.DocumentType);
    }
}
