namespace BuildingBlocks.Application;

/// <summary>
/// The parsed SFS <c>search</c> object: a free-text <paramref name="Query"/> matched against an optional set of
/// <paramref name="Fields"/>. When <paramref name="Fields"/> is null the apply step falls back to the endpoint's
/// search whitelist. (REQ-9.1)
/// </summary>
public sealed record SearchOption(string Query, string[]? Fields = null);
