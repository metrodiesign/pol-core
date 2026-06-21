namespace SharedKernel.Tests;

public class MoneyTests
{
    [Fact]
    public void Of_StoresMinorUnitsAndUpperInvariantCurrency()
    {
        var money = Money.Of(150, "thb");

        Assert.Equal(150, money.MinorUnits);
        Assert.Equal("THB", money.Currency);
    }

    [Fact]
    public void Of_RejectsNegativeMinorUnits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(-1, "THB"));
    }

    [Fact]
    public void Of_RejectsUnknownCurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Of(100, "EUR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Of_RejectsBlankCurrency(string currency)
    {
        Assert.Throws<ArgumentException>(() => Money.Of(100, currency));
    }

    [Fact]
    public void Of_RejectsNullCurrency()
    {
        Assert.Throws<ArgumentNullException>(() => Money.Of(100, null!));
    }

    [Fact]
    public void Of_AllowsZeroMinorUnits()
    {
        var money = Money.Of(0, "JPY");

        Assert.Equal(0, money.MinorUnits);
        Assert.Equal("JPY", money.Currency);
    }

    [Fact]
    public void Zero_HasZeroMinorUnitsInGivenCurrency()
    {
        var zero = Money.Zero("USD");

        Assert.Equal(0, zero.MinorUnits);
        Assert.Equal("USD", zero.Currency);
    }

    [Fact]
    public void Zero_RejectsUnknownCurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Money.Zero("XXX"));
    }

    [Fact]
    public void Add_SameCurrency_SumsMinorUnits()
    {
        var sum = Money.Of(100, "THB").Add(Money.Of(250, "THB"));

        Assert.Equal(350, sum.MinorUnits);
        Assert.Equal("THB", sum.Currency);
    }

    [Fact]
    public void Add_ToZero_ReturnsOriginalAmount()
    {
        var amount = Money.Of(500, "USD");

        var sum = Money.Zero("USD").Add(amount);

        Assert.Equal(amount, sum);
    }

    [Fact]
    public void Add_DifferentCurrencies_Throws()
    {
        var thb = Money.Of(100, "THB");
        var usd = Money.Of(100, "USD");

        Assert.Throws<InvalidOperationException>(() => thb.Add(usd));
    }

    [Fact]
    public void Add_Overflow_ThrowsChecked()
    {
        var big = Money.Of(long.MaxValue, "JPY");
        var one = Money.Of(1, "JPY");

        Assert.Throws<OverflowException>(() => big.Add(one));
    }

    [Fact]
    public void Add_OnDefaultMoney_Throws()
    {
        Money uninitialised = default;

        Assert.Throws<InvalidOperationException>(() => uninitialised.Add(Money.Of(1, "THB")));
    }

    [Fact]
    public void Add_WithDefaultMoneyArgument_Throws()
    {
        var valid = Money.Of(1, "THB");
        Money uninitialised = default;

        Assert.Throws<InvalidOperationException>(() => valid.Add(uninitialised));
    }

    [Fact]
    public void SameCurrencyAs_TrueForMatchingCurrency()
    {
        Assert.True(Money.Of(1, "THB").SameCurrencyAs(Money.Of(999, "THB")));
    }

    [Fact]
    public void SameCurrencyAs_FalseForDifferentCurrency()
    {
        Assert.False(Money.Of(1, "THB").SameCurrencyAs(Money.Of(1, "USD")));
    }

    [Fact]
    public void Equality_SameMinorUnitsAndCurrency_AreEqual()
    {
        Assert.Equal(Money.Of(100, "THB"), Money.Of(100, "thb"));
    }

    [Fact]
    public void Equality_DifferentMinorUnits_AreNotEqual()
    {
        Assert.NotEqual(Money.Of(100, "THB"), Money.Of(101, "THB"));
    }
}
