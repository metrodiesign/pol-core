using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orders.Domain;

/// <summary>Generic client metadata envelope. The schema version is explicit so future policies can evolve
/// without treating an arbitrary JSON blob as a stable contract.</summary>
public sealed record VersionedMetadata(int SchemaVersion, JsonObject Data)
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxBytes = 8 * 1024;
    public const int MaxDepth = 4;
    public const int MaxProperties = 32;
    public const int MaxStringLength = 256;

    public string ToCanonicalJson()
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["data"] = Data.DeepClone(),
        };
        return Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(root));
    }

    public static VersionedMetadata? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var bytes = Encoding.UTF8.GetBytes(json);
        if (bytes.Length > MaxBytes)
            throw new ArgumentException("Versioned metadata exceeds the size limit.", nameof(json));
        var node = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = false })
            ?? throw new ArgumentException("Versioned metadata must be a JSON object.", nameof(json));
        if (node is not JsonObject root)
            throw new ArgumentException("Versioned metadata must be a JSON object.", nameof(json));
        if (!root.TryGetPropertyValue("schemaVersion", out var versionNode)
            || versionNode is not JsonValue versionValue
            || !versionValue.TryGetValue<int>(out var version)
            || version != CurrentSchemaVersion)
            throw new ArgumentException("Versioned metadata schemaVersion is unsupported.", nameof(json));
        if (!root.TryGetPropertyValue("data", out var dataNode) || dataNode is not JsonObject data)
            throw new ArgumentException("Versioned metadata data must be a JSON object.", nameof(json));
        var counter = new UnsafeCounter();
        ValidateNode(data, depth: 0, ref counter);
        var normalized = NormalizeObject(data);
        var result = new VersionedMetadata(version, normalized);
        if (Encoding.UTF8.GetByteCount(result.ToCanonicalJson()) > MaxBytes)
            throw new ArgumentException("Versioned metadata exceeds the size limit.", nameof(json));
        return result;
    }

    private static JsonObject NormalizeObject(JsonObject source)
    {
        var result = new JsonObject();
        foreach (var property in source.OrderBy(x => x.Key, StringComparer.Ordinal))
            result[property.Key] = NormalizeNode(property.Value);
        return result;
    }

    private static JsonNode? NormalizeNode(JsonNode? node) => node switch
    {
        JsonObject obj => NormalizeObject(obj),
        JsonArray array => new JsonArray(array.Select(NormalizeNode).ToArray()),
        _ => node?.DeepClone(),
    };

    private static void ValidateNode(JsonNode node, int depth, ref UnsafeCounter properties)
    {
        if (depth > MaxDepth)
            throw new ArgumentException("Versioned metadata nesting is too deep.");
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    properties.Value++;
                    if (properties.Value > MaxProperties)
                        throw new ArgumentException("Versioned metadata has too many properties.");
                    if (property.Key.Length > MaxStringLength)
                        throw new ArgumentException("Versioned metadata property name is too long.");
                    if (property.Value is not null)
                        ValidateNode(property.Value, depth + 1, ref properties);
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                        ValidateNode(child, depth + 1, ref properties);
                }
                break;
            case JsonValue value when value.TryGetValue<string>(out var text)
                && text.Length > MaxStringLength:
                throw new ArgumentException("Versioned metadata string is too long.");
        }
    }

    private struct UnsafeCounter
    {
        public int Value;
    }
}
