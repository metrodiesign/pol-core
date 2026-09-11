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
    [InlineData(99.99, "USD")]
    [InlineData(1.2345, "THB")]
    public void RoundTrips_SerializeThenDeserialize_EqualsOriginal(double amount, string currency)
    {
        var options = Options();
        var original = Money.Of((decimal)amount, currency);

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<Money>(json, options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Write_EmitsAmountAsStringFixedToFourDecimals()
    {
        var json = JsonSerializer.Serialize(Money.Of(150m, "THB"), Options());

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, doc.RootElement.GetProperty("amount").ValueKind);
        Assert.Equal("150.0000", doc.RootElement.GetProperty("amount").GetString());
        Assert.Equal("THB", doc.RootElement.GetProperty("currency").GetString());
    }

    [Fact]
    public void Read_NormalisesCurrencyThroughMoneyOf()
    {
        var restored = JsonSerializer.Deserialize<Money>("{\"amount\":\"150.0000\",\"currency\":\"thb\"}", Options());

        Assert.Equal("THB", restored.Currency);
        Assert.Equal(150m, restored.Amount);
    }

    [Fact]
    public void Read_ValidatesNegativeAmount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonSerializer.Deserialize<Money>("{\"amount\":\"-1.0000\",\"currency\":\"THB\"}", Options()));
    }

    [Fact]
    public void Read_ValidatesUnknownCurrency()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            JsonSerializer.Deserialize<Money>("{\"amount\":\"100.0000\",\"currency\":\"EUR\"}", Options()));
    }

    [Fact]
    public void Read_ValidatesScaleGreaterThanFour()
    {
        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<Money>("{\"amount\":\"1.23455\",\"currency\":\"THB\"}", Options()));
    }

    [Fact]
    public void Read_RejectsJsonNumberAmount()
    {
        // REQ-6.5: amount MUST be a JSON string (never a number) — guards IEEE754 double precision loss.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>("{\"amount\":150,\"currency\":\"THB\"}", Options()));
    }

    [Fact]
    public void Read_MissingAmount_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>("{\"currency\":\"THB\"}", Options()));
    }

    [Fact]
    public void Read_MissingCurrency_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>("{\"amount\":\"100.0000\"}", Options()));
    }

    [Fact]
    public void Read_NonObjectToken_Throws()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<Money>("\"THB\"", Options()));
    }
}
