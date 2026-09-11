using System.Text.Json;
using System.Text.Json.Serialization;

namespace Merchants.Domain;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MerchantMetadata(
    MerchantBrandingMetadata? Branding = null,
    MerchantRoutingMetadata? Routing = null,
    MerchantSessionMetadata? Session = null,
    string? Timezone = null,
    string? Locale = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MerchantBrandingMetadata(
    string? LogoUrl = null,
    string? StatementName = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MerchantRoutingMetadata(
    IReadOnlyList<string>? Installment = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MerchantSessionMetadata(
    int? TtlSeconds = null);

/// <summary>Canonical allowlist codec for native <c>merch.Merchants.Metadata</c>.</summary>
public static class MerchantMetadataCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static string Serialize(MerchantMetadata? metadata) =>
        JsonSerializer.Serialize(metadata ?? new MerchantMetadata(), Options);

    public static MerchantMetadata Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<MerchantMetadata>(json, Options)
            ?? throw new JsonException("Merchant metadata cannot be null.");
    }

    public static string Canonicalize(string json) => Serialize(Parse(json));

    public static JsonElement ToJsonElement(string json)
    {
        var canonical = Canonicalize(json);
        using var document = JsonDocument.Parse(canonical);
        return document.RootElement.Clone();
    }
}
