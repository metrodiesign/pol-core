using System.Text.Json;

namespace SharedKernel.Tests;

public class MoneyJsonConverterTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MoneyJsonConverter());
        return options;
    }

    [Theory]
    [InlineData(0, "JPY")]
    [InlineData(150, "THB")]
    [InlineData(99, "USD")]
    public void RoundTrips_SerializeThenDeserialize_EqualsOriginal(long minorUnits, string currency)
    {
        var options = Options();
        var original = Money.Of(minorUnits, currency);

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<Money>(json, options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Write_EmitsCamelCaseMinorUnitsAndCurrency()
    {
        var json = JsonSerializer.Serialize(Money.Of(150, "THB"), Options());

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(150, doc.RootElement.GetProperty("minorUnits").GetInt64());
        Assert.Equal("THB", doc.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public void Read_NormalisesCurrencyThroughMoneyOf()
    {
        var restored = JsonSerializer.Deserialize<Money>("{\"minorUnits\":150,\"currency\":\"thb\"}", Options());

        Assert.Equal("THB", restored.Currency);
        Assert.Equal(150, restored.MinorUnits);
    }

    [Fact]
    public void Read_ValidatesNegativeMinorUnits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonSerializer.Deserialize<Money>("{\"minorUnits\":-1,\"currency\":\"THB\"}", Options()));
    }

    [Fact]
    public void Read_ValidatesUnknownCurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonSerializer.Deserialize<Money>("{\"minorUnits\":100,\"currency\":\"EUR\"}", Options()));
    }

    [Fact]
    public void Read_MissingCurrency_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>("{\"minorUnits\":100}", Options()));
    }

    [Fact]
    public void Read_NonObjectToken_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>("\"THB\"", Options()));
    }
}
