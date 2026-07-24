using Orders.Domain;
using Orders.Domain.Lines;
using SharedKernel;

namespace Orders.Tests;

/// <summary>
/// Pure domain tests for <see cref="Order.Create"/>'s line handling (insurance-pivot REQ-6/7) — no DB.
/// Covers the line-sum invariant (6.3), empty-lines rejection (6.7), the quantity==1 constraint, and the
/// insured-person validation on <see cref="Orders.Domain.Lines.Line"/> (7.2), including that the thrown
/// exception never echoes the invalid PII value (7.3).
/// </summary>
public sealed class OrderLinesTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly Guid ProductB = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static OrderLineInput Line(Guid productId, decimal unitPrice, int quantity = 1, string idNumber = "1234567890123") =>
        new(productId, quantity, Money.Of(unitPrice, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", idNumber, Dob);

    [Fact]
    public void Create_with_one_line_matching_the_amount_succeeds()
    {
        var order = Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Line(ProductA, 15000m)]);

        var line = Assert.Single(order.Lines);
        Assert.Equal(ProductA, line.ProductId);
        Assert.Equal(order.Id, line.OrderId);
        Assert.Equal(MerchantId, line.MerchantId);
    }

    [Fact]
    public void Create_with_multiple_lines_summing_to_the_amount_succeeds()
    {
        var order = Order.Create(
            MerchantId, Money.Of(25000m, "THB"), At, [Line(ProductA, 15000m), Line(ProductB, 10000m)]);

        Assert.Equal(2, order.Lines.Count);
    }

    [Fact]
    public void Create_rejects_an_empty_line_list()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, []));
        Assert.Equal("lines", ex.ParamName);
    }

    [Fact]
    public void Create_rejects_a_line_whose_quantity_is_not_1()
    {
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(30000m, "THB"), At, [Line(ProductA, 15000m, quantity: 2)]));
    }

    [Fact]
    public void Create_rejects_a_line_sum_that_does_not_match_the_amount()
    {
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Line(ProductA, 14999m)]));
    }

    [Fact]
    public void Create_rejects_a_line_currency_mismatched_with_the_amount()
    {
        var mismatched = new OrderLineInput(
            ProductA, 1, Money.Of(15000m, "USD"), Money.Of(1_000_000m, "USD"), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", Dob);

        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [mismatched]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_a_blank_insured_IdNumber(string idNumber) =>
        Assert.Throws<ArgumentException>(
            () => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [Line(ProductA, 15000m, idNumber: idNumber)]));

    [Fact]
    public void Create_rejects_a_future_date_of_birth()
    {
        var futureDob = new OrderLineInput(
            ProductA, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", At.AddDays(1));

        Assert.Throws<ArgumentException>(() => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [futureDob]));
    }

    [Fact]
    public void The_thrown_exception_never_echoes_the_invalid_date_of_birth_value()
    {
        var distinctiveFutureDob = new DateTime(2099, 3, 14, 0, 0, 0, DateTimeKind.Utc);
        var bad = new OrderLineInput(
            ProductA, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", distinctiveFutureDob);

        var ex = Assert.Throws<ArgumentException>(() => Order.Create(MerchantId, Money.Of(15000m, "THB"), At, [bad]));

        Assert.DoesNotContain("2099", ex.Message, StringComparison.Ordinal);
    }
}
