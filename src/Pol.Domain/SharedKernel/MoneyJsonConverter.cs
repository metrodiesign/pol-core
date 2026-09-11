using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedKernel;

/// <summary>
/// Serialises <see cref="Money"/> as <c>{ "amount": "1500.0000", "currency": "THB" }</c> (rf1
/// REQ-6.4/6.5) — amount is a STRING fixed to 4 decimal places on write, and MUST be a JSON
/// string (never a number) on read, to keep IEEE754 double precision loss off the wire. Values
/// reconstruct through <see cref="Money.Of"/> so validation (known currency, scale &lt;= 4,
/// non-negative) still runs on read. Used by the outbox payload and any JSON API exposing money.
/// </summary>
public sealed class MoneyJsonConverter : JsonConverter<Money>
{
    public override Money Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected object for Money.");

        decimal? amount = null;
        string? currency = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            var prop = reader.GetString();
            reader.Read();
            switch (prop)
            {
                case "amount":
                    if (reader.TokenType != JsonTokenType.String)
                        throw new JsonException("Money.amount must be a JSON string (e.g. \"1500.0000\"), not a number.");
                    if (!decimal.TryParse(
                            reader.GetString(),
                            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                            CultureInfo.InvariantCulture,
                            out var parsed))
                        throw new JsonException("Money.amount is not a valid decimal string.");
                    amount = parsed;
                    break;
                case "currency":
                    currency = reader.GetString();
                    break;
            }
        }

        if (amount is null)
            throw new JsonException("Money is missing 'amount'.");
        if (currency is null)
            throw new JsonException("Money is missing 'currency'.");

        return Money.Of(amount.Value, currency);
    }

    public override void Write(Utf8JsonWriter writer, Money value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("amount", value.Amount.ToString("F4", CultureInfo.InvariantCulture));
        writer.WriteString("currency", value.Currency);
        writer.WriteEndObject();
    }
}
