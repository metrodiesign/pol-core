using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedKernel;

/// <summary>
/// Serialises <see cref="Money"/> as <c>{ "minorUnits": &lt;long&gt;, "currency": "THB" }</c> and
/// reconstructs it through <see cref="Money.Of"/> so validation (known currency, non-negative)
/// still runs on read. Used by the outbox payload and any JSON API exposing money.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object for Money.");

        long minorUnits = 0;
        string? currency = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "minorUnits":
                    minorUnits = reader.GetInt64();
                    break;
                case "currency":
                    currency = reader.GetString();
                    break;
            }
        }

        if (currency is null)
            throw new JsonException("Money is missing 'currency'.");

        return Money.Of(minorUnits, currency);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("minorUnits", value.MinorUnits);
        writer.WriteString("currency", value.Currency);
        writer.WriteEndObject();
    }
}
