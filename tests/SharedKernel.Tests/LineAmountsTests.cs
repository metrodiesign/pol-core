using SharedKernel;

namespace SharedKernel.Tests;

/// <summary>purchase-flow-completion REQ-6.3/6.4 — the per-line money rule, shared by Checkouts, Orders and
/// the checkout endpoint so the three cannot drift.</summary>
public sealed class LineAmountsTests
{
    private static readonly Money Thb1500 = Money.Of(1500m, "THB");

    [Fact]
    public void Gross_is_unit_price_times_quantity() =>
        Assert.Equal(Money.Of(3000m, "THB"), LineAmounts.Gross(Thb1500, 2));

    [Fact]
    public void A_missing_discount_becomes_zero_in_the_lines_currency()
    {
        var discount = LineAmounts.NormaliseDiscount(null, Money.Of(1500m, "USD"));

        Assert.Equal(0m, discount.Amount);
        Assert.Equal("USD", discount.Currency);
    }

    [Fact]
    public void A_discount_equal_to_the_line_is_allowed() =>
        Assert.Equal(Thb1500, LineAmounts.NormaliseDiscount(Thb1500, Thb1500));

    // REQ-6.4 — over the line total.
    [Fact]
    public void A_discount_larger_than_the_line_is_rejected() =>
        Assert.Throws<ArgumentException>(() => LineAmounts.NormaliseDiscount(Money.Of(1500.01m, "THB"), Thb1500));

    // REQ-6.4 — negative is impossible one layer down; Money itself refuses to hold one.
    [Fact]
    public void A_negative_discount_cannot_even_be_constructed() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(-1m, "THB"));

    [Fact]
    public void A_discount_in_another_currency_is_rejected() =>
        Assert.Throws<ArgumentException>(() => LineAmounts.NormaliseDiscount(Money.Of(10m, "USD"), Thb1500));

    [Fact]
    public void Net_is_gross_minus_discount() =>
        Assert.Equal(Money.Of(1400m, "THB"), LineAmounts.Net(Thb1500, Money.Of(100m, "THB")));
}
