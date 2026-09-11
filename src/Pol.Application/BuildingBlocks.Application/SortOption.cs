namespace BuildingBlocks.Application;

/// <summary>
/// One parsed sort clause from the SFS <c>sort</c> array. Clauses apply in array order. NULLS-last is an
/// ordering invariant enforced at apply time, so there is no per-clause nulls flag here. (REQ-9.1)
/// </summary>
public sealed record SortOption(string Field, SortDirection Order = SortDirection.Asc);
