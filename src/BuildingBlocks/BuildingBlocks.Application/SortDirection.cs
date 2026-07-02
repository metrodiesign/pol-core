using System.Text.Json.Serialization;

namespace BuildingBlocks.Application;

/// <summary>
/// Sort order on the SFS query contract, carried on the wire as the literal strings <c>"ASC"</c> / <c>"DESC"</c>.
/// The converter is annotated on the enum for the same reason as <see cref="FilterOperator"/>: the host has no
/// global string-enum converter, so without it <c>"order":"ASC"</c> would fail to bind. (REQ-1.4, REQ-9.2)
/// </summary>
[JsonConverter(typeof(SortDirectionJsonConverter))]
public enum SortDirection
{
    [JsonStringEnumMemberName("ASC")] Asc,
    [JsonStringEnumMemberName("DESC")] Desc,
}

/// <summary>
/// String-token converter for <see cref="SortDirection"/> with <c>allowIntegerValues: false</c>, so a numeric
/// order (e.g. <c>{"order":0}</c>) is rejected (400) rather than accepted as the enum ordinal — the wire
/// contract is the literal <c>"ASC"</c>/<c>"DESC"</c> (REQ-1.4).
/// </summary>
internal sealed class SortDirectionJsonConverter()
    : JsonStringEnumConverter<SortDirection>(namingPolicy: null, allowIntegerValues: false);
