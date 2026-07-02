namespace BuildingBlocks.Application;

/// <summary>
/// A page of results plus the paging envelope. <paramref name="Total"/> is the count after filter and search
/// but before <c>Skip</c>/<c>Take</c>. When an endpoint maps rows to a wire DTO it must construct a new
/// <see cref="PagedResult{T}"/> — a record <c>with</c>-expression cannot change the item type. (REQ-2.4, REQ-2.7)
/// </summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Limit, long Total)
{
    /// <summary><c>ceil(Total / Limit)</c>; 0 when <see cref="Limit"/> is non-positive (guards divide-by-zero).</summary>
    public int TotalPages => Limit <= 0 ? 0 : (int)Math.Ceiling((double)Total / Limit);
}
