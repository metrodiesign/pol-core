using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharedKernel;

/// <summary>
/// Server-owned commerce facts persisted with a cart/order line. Deliberately contains no insured/customer PII
/// and no extension bag; adding a field requires changing this contract and its privacy review.
/// </summary>
public sealed record CommerceItemMetadata(
    string SourceType,
    string? DocumentType,
    string? PolicyNumber,
    DateOnly? StartDate,
    DateOnly? EndDate);

/// <summary>Single canonical serializer/parser for commerce line metadata.</summary>
public static class CommerceItemMetadataCodec
{
    public const string InsuranceDocumentSource = "insurance_document";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(CommerceItemMetadata metadata)
    {
        Validate(metadata);
        return JsonSerializer.Serialize(metadata, Options);
    }

    public static CommerceItemMetadata Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var metadata = JsonSerializer.Deserialize<CommerceItemMetadata>(json, Options)
            ?? throw new JsonException("Commerce item metadata cannot be null.");
        Validate(metadata);
        return metadata;
    }

    public static JsonElement ToJsonElement(string json)
    {
        _ = Parse(json);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void Validate(CommerceItemMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!string.Equals(metadata.SourceType, InsuranceDocumentSource, StringComparison.Ordinal))
            throw new ArgumentException("Unsupported commerce metadata source type.", nameof(metadata));
        if (metadata.StartDate is not null && metadata.EndDate is not null && metadata.StartDate > metadata.EndDate)
            throw new ArgumentException("Metadata start date must not be after end date.", nameof(metadata));
    }
}
