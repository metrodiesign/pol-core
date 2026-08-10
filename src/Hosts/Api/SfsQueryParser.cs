using System.Text.Json;
using BuildingBlocks.Application;
using SearchOption = BuildingBlocks.Application.SearchOption;   // disambiguate from System.IO.SearchOption

namespace Api;

/// <summary>
/// Parses the SFS query-string contract (<c>page</c>, <c>limit</c>, <c>filters</c>, <c>sort</c>, <c>search</c>)
/// at the Hosts layer — the only layer that knows <see cref="IQueryCollection"/> — into the shared
/// <see cref="BuildingBlocks.Application"/> value types. <c>page</c>/<c>limit</c> are clamped (never a 400);
/// malformed JSON and over-cap requests throw <see cref="ArgumentException"/>, which
/// <c>ProblemDetailsExceptionHandler</c> maps to 400 (never <c>BadHttpRequestException</c>, an
/// <c>IOException</c> that would map to an opaque 500). Uses <see cref="JsonSerializerDefaults.Web"/> and adds
/// no dependency beyond <c>System.Text.Json</c>. (REQ-1, REQ-2, REQ-6.6, REQ-8.1)
/// </summary>
internal static class SfsQueryParser
{
    // Query-cost caps (REQ-6.6): bound expression/SQL size and stay under SQL Server's ~2100 parameter limit.
    // Page-size ceiling (REQ-4.1): SP §2 @PageSize caps at 25 and §5.1 restates it.
    private const int DefaultMaxLimit = 25;

    private const int MaxFilters = 50;
    private const int MaxSortKeys = 10;
    private const int MaxValues = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses just the paging pair, for endpoints that have a typed filter surface of their own and
    /// no SFS surface at all (products, whose §2 input contract has no filter/sort/search — REQ-7.1).</summary>
    public static (int Page, int Limit) ParsePaging(IQueryCollection query, int maxLimit = DefaultMaxLimit)
    {
        if (maxLimit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(maxLimit), "Endpoint max limit must be in 1..100.");

        var limit = Math.Clamp(TryInt(query["limit"], 25), 1, maxLimit);   // clamp = safety, not a 400 (REQ-2.2)
        var page = ClampPage(TryInt(query["page"], 1), limit);        // >= 1 + offset ceiling (REQ-2.3, REQ-2.6)
        return (page, limit);
    }

    public static (int Page, int Limit, IReadOnlyList<FilterOption> Filters,
                   IReadOnlyList<SortOption> Sort, SearchOption? Search) Parse(
        IQueryCollection query, int maxLimit = DefaultMaxLimit)
    {
        var (page, limit) = ParsePaging(query, maxLimit);

        var filters = Deserialize<List<FilterOption>>(query["filters"]) ?? [];
        if (filters.Count > MaxFilters)
            throw new ArgumentException("Too many filters.");
        foreach (var f in filters)
            if (f.Values is { Length: > MaxValues })
                throw new ArgumentException("Too many filter values.");

        var sort = Deserialize<List<SortOption>>(query["sort"]) ?? [];
        if (sort.Count > MaxSortKeys)
            throw new ArgumentException("Too many sort keys.");

        var search = Deserialize<SearchOption>(query["search"]);

        return (page, limit, filters, sort, search);
    }

    // Clamp page to >= 1 AND to the offset ceiling so (long)(page-1)*limit is always non-negative and within
    // int range: a non-positive page becomes 1; a huge page can never overflow int into a negative SQL OFFSET
    // (a client-triggerable 500). Computed in long so limit==1 (ceiling beyond int.MaxValue) is handled. (REQ-2.6)
    private static int ClampPage(int page, int limit)
    {
        if (page < 1) return 1;
        long maxPage = (long)int.MaxValue / limit + 1;   // largest page whose (page-1)*limit <= int.MaxValue
        return page > maxPage ? (int)maxPage : page;
    }

    private static T? Deserialize<T>(string? raw) where T : class
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return JsonSerializer.Deserialize<T>(raw, Json); }
        catch (JsonException ex)
        {
            // Malformed JSON is an identifiable client error -> ArgumentException (400 via the shared handler).
            // Must NOT be BadHttpRequestException: it derives from IOException and maps to an opaque 500. (REQ-1.5, REQ-8.1)
            throw new ArgumentException("Malformed SFS query parameter.", ex);
        }
    }

    private static int TryInt(string? s, int fallback) => int.TryParse(s, out var v) ? v : fallback;
}
