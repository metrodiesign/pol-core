using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Governance.Domain;

public static class AuditRedactor
{
    private static readonly string[] SensitiveTerms =
    [
        "secret", "token", "password", "credential", "authorization", "cookie", "csrf", "privatekey",
        "apikey", "identitynumber", "personalidentifier", "nationalid", "taxid", "cardnumber", "cvv", "cvc",
        "bankaccount", "phone", "email",
    ];

    public static string RedactAndCanonicalize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (Encoding.UTF8.GetByteCount(json) > 32_768)
            throw new ArgumentException("Audit input exceeds 32 KiB.", nameof(json));

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, document.RootElement);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitive(property.Name))
                        writer.WriteStringValue("[REDACTED]");
                    else
                        Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return SensitiveTerms.Any(normalized.Contains);
    }
}
