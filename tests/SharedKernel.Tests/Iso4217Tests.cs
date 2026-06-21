namespace SharedKernel.Tests;

public class Iso4217Tests
{
    [Theory]
    [InlineData("THB")]
    [InlineData("USD")]
    [InlineData("JPY")]
    public void IsSupported_TrueForRegisteredCurrencies(string code)
    {
        Assert.True(Iso4217.IsSupported(code));
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("XXX")]
    [InlineData("thb")] // case-sensitive: lookup is ordinal upper-invariant
    public void IsSupported_FalseForUnregisteredOrWrongCase(string code)
    {
        Assert.False(Iso4217.IsSupported(code));
    }

    [Theory]
    [InlineData("THB", 2)]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    public void MinorUnitDigits_ReturnsExponentForCurrency(string code, int expectedDigits)
    {
        Assert.Equal(expectedDigits, Iso4217.MinorUnitDigits(code));
    }

    [Fact]
    public void MinorUnitDigits_UnknownCurrency_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Iso4217.MinorUnitDigits("EUR"));
    }

    [Fact]
    public void Supported_ContainsTheRegisteredCurrencies()
    {
        Assert.Equal(new[] { "JPY", "THB", "USD" }, Iso4217.Supported.OrderBy(c => c, StringComparer.Ordinal));
    }
}
