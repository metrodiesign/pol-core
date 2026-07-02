using System.Text.Json.Serialization;

namespace BuildingBlocks.Application;

/// <summary>
/// Sort order on the SFS query contract, carried on the wire as the literal strings <c>"ASC"</c> / <c>"DESC"</c>.
/// The converter is annotated on the enum for the same reason as <see cref="FilterOperator"/>: the host has no
/// global string-enum converter, so without it <c>"order":"ASC"</c> would fail to bind. (REQ-1.4, REQ-9.2)
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SortDirection>))]
public enum SortDirection
{
    [JsonStringEnumMemberName("ASC")] Asc,
    [JsonStringEnumMemberName("DESC")] Desc,
}
