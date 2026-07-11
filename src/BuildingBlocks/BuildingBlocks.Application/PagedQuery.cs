namespace BuildingBlocks.Application;

/// <summary>
/// Base for every paged/SFS query. A module query record inherits this and adds its own fields (e.g. a merchant
/// id or a typed filter DTO). Defaults mirror an absent query string: page 1, limit 25, no filters, no sort,
/// no search. Parser-level clamping of <see cref="Page"/>/<see cref="Limit"/> happens at the Hosts layer.
/// (REQ-2.1, REQ-1.6, REQ-9.1)
/// </summary>
public abstract record PagedQuery
{
    public int Page { get; init; } = 1;
    public int Limit { get; init; } = 25;
    public IReadOnlyList<FilterOption> Filters { get; init; } = [];
    public IReadOnlyList<SortOption> Sort { get; init; } = [];
    public SearchOption? Search { get; init; }
}
