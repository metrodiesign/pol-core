using System.Text.Json.Serialization;

namespace BuildingBlocks.Application;

/// <summary>
/// The filter operators accepted on the SFS query contract, (de)serialized as their lowercase/snake wire
/// tokens (<c>eq</c>, <c>not_in</c>, <c>is_null</c>, …). The converter is annotated directly on the enum
/// because the host registers no global string-enum converter; without it the Web defaults would
/// (de)serialize these as integers and reject the wire tokens. (REQ-1.3, REQ-9.2)
/// </summary>
[JsonConverter(typeof(FilterOperatorJsonConverter))]
public enum FilterOperator
{
    [JsonStringEnumMemberName("eq")] Equals,
    [JsonStringEnumMemberName("ne")] NotEquals,
    [JsonStringEnumMemberName("gt")] GreaterThan,
    [JsonStringEnumMemberName("gte")] GreaterThanOrEqual,
    [JsonStringEnumMemberName("lt")] LessThan,
    [JsonStringEnumMemberName("lte")] LessThanOrEqual,
    [JsonStringEnumMemberName("like")] Like,
    [JsonStringEnumMemberName("ilike")] ILike,
    [JsonStringEnumMemberName("in")] In,
    [JsonStringEnumMemberName("not_in")] NotIn,
    [JsonStringEnumMemberName("is_null")] IsNull,
    [JsonStringEnumMemberName("is_not_null")] IsNotNull,
    [JsonStringEnumMemberName("between")] Between,
    [JsonStringEnumMemberName("contains")] Contains,
}

/// <summary>
/// String-token converter for <see cref="FilterOperator"/> with <c>allowIntegerValues: false</c>: a numeric
/// operator token (e.g. <c>{"operator":0}</c>) is rejected as a <see cref="System.Text.Json.JsonException"/>
/// (surfaced by the parser as a 400) instead of being silently accepted as the enum ordinal. The default
/// <see cref="JsonStringEnumConverter{TEnum}"/> still permits integers, which would bypass REQ-1.3.
/// </summary>
internal sealed class FilterOperatorJsonConverter()
    : JsonStringEnumConverter<FilterOperator>(namingPolicy: null, allowIntegerValues: false);
