namespace SharedKernel.Tests;

public class MoneyTests
{
    [Fact]
    public void Of_StoresAmountAndUpperInvariantCurrency()
    {
        var money = Money.Of(150m, "thb");

        Assert.Equal(150m, money.Amount);
        Assert.Equal("THB", money.Currency);
    }

    [Fact]
    public void Of_RejectsNegativeAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(-1m, "THB"));
    }

    [Fact]
    public void Of_RejectsUnknownCurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(100m, "EUR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Of_RejectsBlankCurrency(string currency)
    {
        Assert.Throws<ArgumentException>(() => Money.Of(100m, currency));
    }

    [Fact]
    public void Of_RejectsNullCurrency()
    {
        Assert.Throws<ArgumentNullException>(() => Money.Of(100m, null!));
    }

    [Fact]
    public void Of_AllowsZeroAmount()
    {
        var money = Money.Of(0m, "JPY");

        Assert.Equal(0m, money.Amount);
        Assert.Equal("JPY", money.Currency);
    }

    [Fact]
    public void Of_AllowsScaleUpToFour()
    {
        var money = Money.Of(1.2345m, "THB");

        Assert.Equal(1.2345m, money.Amount);
    }

    [Theory]
    [InlineData(1.23455)]
    [InlineData(0.00001)]
    public void Of_RejectsScaleGreaterThanFour(double amount)
    {
        Assert.Throws<ArgumentException>(() => Money.Of((decimal)amount, "THB"));
    }

    [Fact]
    public void Zero_HasZeroAmountInGivenCurrency()
    {
        var zero = Money.Zero("USD");

        Assert.Equal(0m, zero.Amount);
        Assert.Equal("USD", zero.Currency);
    }

    [Fact]
    public void Zero_RejectsUnknownCurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Zero("XXX"));
    }

    [Fact]
    public void Add_SameCurrency_SumsAmount()
    {
        var sum = Money.Of(100m, "THB").Add(Money.Of(250m, "THB"));

        Assert.Equal(350m, sum.Amount);
        Assert.Equal("THB", sum.Currency);
    }

    [Fact]
    public void Add_ToZero_ReturnsOriginalAmount()
    {
        var amount = Money.Of(500m, "USD");

        var sum = Money.Zero("USD").Add(amount);

        Assert.Equal(amount, sum);
    }

    [Fact]
    public void Add_DifferentCurrencies_Throws()
    {
        var thb = Money.Of(100m, "THB");
        var usd = Money.Of(100m, "USD");

        Assert.Throws<InvalidOperationException>(() => thb.Add(usd));
    }

    [Fact]
    public void Add_Overflow_Throws()
    {
        var big = Money.Of(decimal.MaxValue, "JPY");
        var one = Money.Of(1m, "JPY");

        Assert.Throws<OverflowException>(() => big.Add(one));
    }

    [Fact]
    public void Add_OnDefaultMoney_Throws()
    {
        Money uninitialised = default;

        Assert.Throws<InvalidOperationException>(() => uninitialised.Add(Money.Of(1m, "THB")));
    }

    [Fact]
    public void Add_WithDefaultMoneyArgument_Throws()
    {
        var valid = Money.Of(1m, "THB");
        Money uninitialised = default;

        Assert.Throws<InvalidOperationException>(() => valid.Add(uninitialised));
    }

    [Fact]
    public void SameCurrencyAs_TrueForMatchingCurrency()
    {
        Assert.True(Money.Of(1m, "THB").SameCurrencyAs(Money.Of(999m, "THB")));
    }

    [Fact]
    public void SameCurrencyAs_FalseForDifferentCurrency()
    {
        Assert.False(Money.Of(1m, "THB").SameCurrencyAs(Money.Of(1m, "USD")));
    }

    [Fact]
    public void Equality_SameAmountAndCurrency_AreEqual()
    {
        Assert.Equal(Money.Of(100m, "THB"), Money.Of(100m, "thb"));
    }

    [Fact]
    public void Equality_DifferentAmount_AreNotEqual()
    {
        Assert.NotEqual(Money.Of(100m, "THB"), Money.Of(101m, "THB"));
    }
}
