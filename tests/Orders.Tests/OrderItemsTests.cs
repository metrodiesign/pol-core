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
    private static readonly DateTime At = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Default-param helper so a signature change touches one line, not every call site. The line is now keyed
    // by DocumentNo (the surrogate ProductId is gone — products-external-source-of-truth REQ-2.2).
    private static OrderItemInput Item(
        decimal unitPrice, int quantity = 1, string idNumber = "1234567890123",
        string currency = "THB", DateTime? dob = null,
        string documentNo = "00098-69100/กธ/900001-10", string productGroup = "VMI", string documentType = "POLICY",
        string? policyNumber = null, DateTime? startDate = null, DateTime? endDate = null) =>
        new(quantity, Money.Of(unitPrice, currency),
            documentNo, productGroup, documentType, policyNumber, startDate, endDate,
            "Somchai", "Jaidee", idNumber, dob ?? Dob);

    [Fact]
    public void Create_with_one_line_matching_the_amount_succeeds()
    {
        var order = Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Item(15000m)], orderNo: "ORD6900000001");

        var item = Assert.Single(order.Items);
        Assert.Equal("00098-69100/กธ/900001-10", item.DocumentNo);
        Assert.Equal(order.Id, item.OrderId);
        Assert.Equal(MerchantId, item.MerchantId);
    }

    [Fact]
    public void Create_with_multiple_lines_summing_to_the_amount_succeeds()
    {
        var order = Order.Create(
            MerchantId, Money.Of(25000m, "THB"), At,
            [Item(15000m, documentNo: "DOC-A"), Item(10000m, documentNo: "DOC-B")], orderNo: "ORD6900000002");

        Assert.Equal(2, order.Items.Count);
    }

    [Fact]
    public void Create_rejects_an_empty_line_list()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [], orderNo: "ORD6900000003"));
        Assert.Equal("items", ex.ParamName);
    }

    [Fact]
    public void Create_rejects_a_line_whose_quantity_is_not_1()
    {
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(30000m, "THB"), At, [Item(15000m, quantity: 2)], orderNo: "ORD6900000004"));
    }

    [Fact]
    public void Create_rejects_a_line_sum_that_does_not_match_the_amount()
    {
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Item(14999m)], orderNo: "ORD6900000005"));
    }

    [Fact]
    public void Create_rejects_a_line_currency_mismatched_with_the_amount()
    {
        var mismatched = Item(15000m, currency: "USD");

        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [mismatched], orderNo: "ORD6900000006"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_insured_IdNumber(string idNumber) =>
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Item(15000m, idNumber: idNumber)], orderNo: "ORD6900000007"));

    [Fact]
    public void Create_rejects_a_future_date_of_birth()
    {
        var futureDob = Item(15000m, dob: At.AddDays(1));

        Assert.Throws<ArgumentException>(() => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [futureDob], orderNo: "ORD6900000008"));
    }

    [Fact]
    public void The_thrown_exception_never_echoes_the_invalid_date_of_birth_value()
    {
        var distinctiveFutureDob = new DateTime(2099, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var bad = Item(15000m, dob: distinctiveFutureDob);

        var ex = Assert.Throws<ArgumentException>(() => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [bad], orderNo: "ORD6900000009"));

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
            [Item(15000m, documentNo: documentNo, productGroup: productGroup, documentType: documentType)], orderNo: "ORD6900000010"));

    [Fact]
    public void Create_rejects_a_start_date_after_the_end_date()
    {
        var ex = Assert.Throws<ArgumentException>(() => Order.Create(
            MerchantId, Money.Of(15000m, "THB"), At,
            [Item(15000m, startDate: At.AddDays(10), endDate: At)], orderNo: "ORD6900000011"));

        Assert.Equal("startDate", ex.ParamName);
    }

    [Fact]
    public void Document_fields_are_trimmed_onto_the_line()
    {
        var order = Order.Create(
            MerchantId, Money.Of(15000m, "THB"), At,
            [Item(15000m, documentNo: "  DOC-1  ", productGroup: " VMI ", documentType: " POLICY ")], orderNo: "ORD6900000012");

        var item = Assert.Single(order.Items);
        Assert.Equal("DOC-1", item.DocumentNo);
        Assert.Equal("VMI", item.ProductGroup);
        Assert.Equal("POLICY", item.DocumentType);
    }
}
