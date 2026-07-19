using System.Collections.Frozen;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Products.Domain;
using SearchOption = BuildingBlocks.Application.SearchOption;   // disambiguate from System.IO.SearchOption

namespace Persistence.MerchantRuntime.Products;

/// <summary>
/// The SFS apply pipeline for the merchant-scoped Product list. Moved from
/// <c>Products.Infrastructure.ProductSfs</c> (task 8.5.3, unchanged) — its only consumer,
/// <see cref="ProductRepository"/>, now lives here since <c>MerchantRuntimeDbContext</c> is internal to this
/// assembly. This exemplar exercises the range/numeric operators (gt, gte, lt, lte, between on
/// <c>priceAmount</c>/<c>createdAt</c>) plus eq on the bool <c>isActive</c>. No whitelist exposes
/// <c>merchantId</c> or any cross-aggregate key, so SFS can only narrow within the merchant floor, never widen
/// it (REQ-7.3). Filter values are coerced from <see cref="JsonElement"/> eagerly and guarded, so a type
/// mismatch is a 400 (<see cref="ArgumentException"/>), never a 409/500. (REQ-3..6, REQ-8.5, REQ-8.6)
/// </summary>
internal static class ProductSfs
{
    private static readonly FrozenDictionary<string, FilterOperator[]> FilterFields =
        new Dictionary<string, FilterOperator[]>(StringComparer.Ordinal)
        {
            ["isActive"] = [FilterOperator.Equals],
            ["priceAmount"] = [FilterOperator.Equals, FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual,
                               FilterOperator.LessThan, FilterOperator.LessThanOrEqual, FilterOperator.Between],
            ["createdAt"] = [FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual,
                             FilterOperator.LessThan, FilterOperator.LessThanOrEqual, FilterOperator.Between],
        }.ToFrozenDictionary(StringComparer.Ordinal);
    // NB: no "merchantId" (or any cross-aggregate FK) in any whitelist — SFS must never widen merchant scope (REQ-7.3).

    private static readonly FrozenSet<string> SortFields =
        new[] { "name", "priceAmount", "createdAt" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SearchFields =
        new[] { "name" }.ToFrozenSet(StringComparer.Ordinal);

    public static IQueryable<Product> ApplyFilters(
        this IQueryable<Product> query, IReadOnlyList<FilterOption> filters, ILogger? logger = null)
    {
        foreach (var f in filters)
        {
            if (!FilterFields.TryGetValue(f.Field, out var allowed))
            {
                logger?.LogDebug("SFS filter dropped: unknown field {Field}", f.Field);
                continue;
            }
            if (!allowed.Contains(f.Operator))
            {
                logger?.LogDebug("SFS filter dropped: operator {Operator} not allowed on field {Field}", f.Operator, f.Field);
                continue;
            }
            query = ApplyFilter(query, f);
        }
        return query;
    }

    private static IQueryable<Product> ApplyFilter(IQueryable<Product> q, FilterOption f)
    {
        switch (f.Field, f.Operator)
        {
            case ("isActive", FilterOperator.Equals): { var b = Bool(f.Value); return q.Where(p => p.IsActive == b); }

            case ("priceAmount", FilterOperator.Equals): { var v = Decimal(f.Value); return q.Where(p => p.Price.Amount == v); }
            case ("priceAmount", FilterOperator.GreaterThan): { var v = Decimal(f.Value); return q.Where(p => p.Price.Amount > v); }
            case ("priceAmount", FilterOperator.GreaterThanOrEqual): { var v = Decimal(f.Value); return q.Where(p => p.Price.Amount >= v); }
            case ("priceAmount", FilterOperator.LessThan): { var v = Decimal(f.Value); return q.Where(p => p.Price.Amount < v); }
            case ("priceAmount", FilterOperator.LessThanOrEqual): { var v = Decimal(f.Value); return q.Where(p => p.Price.Amount <= v); }
            case ("priceAmount", FilterOperator.Between) when f.Values is { Length: >= 2 }:
            { var lo = Decimal(f.Values[0]); var hi = Decimal(f.Values[1]); return q.Where(p => p.Price.Amount >= lo && p.Price.Amount <= hi); }

            case ("createdAt", FilterOperator.GreaterThan): { var v = Date(f.Value); return q.Where(p => p.CreatedAt > v); }
            case ("createdAt", FilterOperator.GreaterThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.CreatedAt >= v); }
            case ("createdAt", FilterOperator.LessThan): { var v = Date(f.Value); return q.Where(p => p.CreatedAt < v); }
            case ("createdAt", FilterOperator.LessThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.CreatedAt <= v); }
            case ("createdAt", FilterOperator.Between) when f.Values is { Length: >= 2 }:
            { var lo = Date(f.Values[0]); var hi = Date(f.Values[1]); return q.Where(p => p.CreatedAt >= lo && p.CreatedAt <= hi); }

            // Reached only when a Between value array is empty/short — silent-drop (REQ-3.6).
            default: return q;
        }
    }

    public static IQueryable<Product> ApplySort(
        this IQueryable<Product> query, IReadOnlyList<SortOption> sort, ILogger? logger = null)
    {
        IOrderedQueryable<Product>? o = null;
        foreach (var s in sort)
        {
            if (!SortFields.Contains(s.Field))
            {
                logger?.LogDebug("SFS sort dropped: unknown field {Field}", s.Field);
                continue;
            }
            bool asc = s.Order == SortDirection.Asc;
            o = (s.Field, first: o is null) switch
            {
                // All three sort columns are non-nullable -> plain ordering (no NULLS-last step needed).
                ("name", true) => asc ? query.OrderBy(p => p.Name) : query.OrderByDescending(p => p.Name),
                ("name", false) => asc ? o!.ThenBy(p => p.Name) : o!.ThenByDescending(p => p.Name),
                ("priceAmount", true) => asc ? query.OrderBy(p => p.Price.Amount) : query.OrderByDescending(p => p.Price.Amount),
                ("priceAmount", false) => asc ? o!.ThenBy(p => p.Price.Amount) : o!.ThenByDescending(p => p.Price.Amount),
                ("createdAt", true) => asc ? query.OrderBy(p => p.CreatedAt) : query.OrderByDescending(p => p.CreatedAt),
                ("createdAt", false) => asc ? o!.ThenBy(p => p.CreatedAt) : o!.ThenByDescending(p => p.CreatedAt),
                _ => o,
            };
        }
        // Default fallback (REQ-4.5). CreatedAt is not unique (bulk/seed ties), so append the unique Id as a
        // tie-breaker — without it, tied timestamps let SQL Server order rows arbitrarily and paging can
        // duplicate/skip items across pages.
        return o ?? query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id);
    }

    public static IQueryable<Product> ApplySearch(this IQueryable<Product> query, SearchOption? search)
    {
        if (search is null || string.IsNullOrWhiteSpace(search.Query)) return query;

        var fields = (search.Fields is { Length: > 0 } requested ? requested : SearchFields.ToArray())
            .Where(SearchFields.Contains).ToArray();
        if (fields.Length == 0) return query;   // only "name" is searchable; a request for anything else drops out

        var pattern = $"%{SfsLike.Escape(search.Query.Trim())}%";
        return query.Where(p => EF.Functions.Like(p.Name, pattern, "\\"));
    }

    private static decimal Decimal(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.Number } element && element.TryGetDecimal(out var n)) return n;
        throw new ArgumentException("Filter value must be a number.");
    }

    private static DateTime Date(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } element && element.TryGetDateTime(out var d)) return d;
        throw new ArgumentException("Filter value must be a date.");
    }

    private static bool Bool(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.True or JsonValueKind.False } element) return element.GetBoolean();
        throw new ArgumentException("Filter value must be a boolean.");
    }
}
