using System.Collections.Frozen;
using System.Text.Json;
using Admins.Domain;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SearchOption = BuildingBlocks.Application.SearchOption;   // disambiguate from System.IO.SearchOption

namespace Admins.Infrastructure.Persistence;

/// <summary>
/// The SFS apply pipeline for the admin directory (admin-account-management REQ-1) — a sibling of
/// <see cref="AdminRoleSfs"/>. Deny-by-default whitelists (<see cref="FrozenDictionary{TKey,TValue}"/> /
/// <see cref="FrozenSet{T}"/>) gate which field+operator pairs reach SQL; every predicate is a compile-checked,
/// parameterized EF Core <c>.Where</c>/ordering. Filter values are coerced from <see cref="JsonElement"/> eagerly
/// and guarded, so a type mismatch or an out-of-domain <c>tier</c>/<c>status</c> is a 400
/// (<see cref="ArgumentException"/>) rather than a 409/500 (REQ-1.7). Unlike AdminRoleSfs, EVERY ordering is
/// closed with a final <c>ThenBy(Id)</c> so paging stays stable when a sorted column collides (REQ-1.3).
/// </summary>
public static class PlatformUserSfs
{
    // Deny-by-default filter whitelist, Ordinal-matched so a wrong-case field is treated as absent (REQ-1.8).
    private static readonly FrozenDictionary<string, FilterOperator[]> FilterFields =
        new Dictionary<string, FilterOperator[]>(StringComparer.Ordinal)
        {
            ["email"] = [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.In, FilterOperator.NotIn,
                         FilterOperator.Like, FilterOperator.ILike, FilterOperator.Contains],
            ["tier"] = [FilterOperator.Equals, FilterOperator.In],
            ["status"] = [FilterOperator.Equals, FilterOperator.In],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SortFields =
        new[] { "email", "createdAt" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SearchFields =
        new[] { "email" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Applies the whitelisted filters as AND-combined parameterized predicates. Unknown fields,
    /// disallowed operators, and empty set values are silently dropped and logged by name at debug (REQ-1.8).</summary>
    public static IQueryable<PlatformUser> ApplyFilters(
        this IQueryable<PlatformUser> query, IReadOnlyList<FilterOption> filters, ILogger? logger = null)
    {
        foreach (var f in filters)
        {
            if (!FilterFields.TryGetValue(f.Field, out var allowed))
            {
                logger?.LogDebug("SFS filter dropped: unknown field {Field}", f.Field);   // name only, never the value
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

    private static IQueryable<PlatformUser> ApplyFilter(IQueryable<PlatformUser> q, FilterOption f)
    {
        switch (f.Field, f.Operator)
        {
            case ("email", FilterOperator.Equals): { var v = Str(f.Value); return q.Where(a => a.Email == v); }
            case ("email", FilterOperator.NotEquals): { var v = Str(f.Value); return q.Where(a => a.Email != v); }
            case ("email", FilterOperator.In) when f.Values is { Length: > 0 }:
            { var vs = f.Values.Select(v => Str(v)).ToArray(); return q.Where(a => vs.Contains(a.Email)); }
            case ("email", FilterOperator.NotIn) when f.Values is { Length: > 0 }:
            { var vs = f.Values.Select(v => Str(v)).ToArray(); return q.Where(a => !vs.Contains(a.Email)); }
            case ("email", FilterOperator.Like):
            case ("email", FilterOperator.ILike):
            { var p = SfsLike.Escape(Str(f.Value)); return q.Where(a => EF.Functions.Like(a.Email, p, "\\")); }
            case ("email", FilterOperator.Contains):
            { var p = $"%{SfsLike.Escape(Str(f.Value))}%"; return q.Where(a => EF.Functions.Like(a.Email, p, "\\")); }

            case ("tier", FilterOperator.Equals): { var t = ParseTier(Str(f.Value)); return q.Where(a => a.Tier == t); }
            case ("tier", FilterOperator.In) when f.Values is { Length: > 0 }:
            { var set = f.Values.Select(v => ParseTier(Str(v))).ToArray(); return q.Where(a => set.Contains(a.Tier)); }

            case ("status", FilterOperator.Equals): { var s = ParseStatus(Str(f.Value)); return q.Where(a => a.Status == s); }
            case ("status", FilterOperator.In) when f.Values is { Length: > 0 }:
            { var set = f.Values.Select(v => ParseStatus(Str(v))).ToArray(); return q.Where(a => set.Contains(a.Status)); }

            // Reached only when a set value is empty (the `when` guards above failed) — silent-drop (REQ-1.8).
            default: return q;
        }
    }

    /// <summary>Orders by the whitelisted sort keys in the order given, then closes EVERY chain with a single
    /// final <c>ThenBy(a =&gt; a.Id)</c> so paging is stable when a sorted column collides (REQ-1.3). No surviving
    /// key falls back to the mandatory default (newest first).</summary>
    public static IQueryable<PlatformUser> ApplySort(
        this IQueryable<PlatformUser> query, IReadOnlyList<SortOption> sort, ILogger? logger = null)
    {
        IOrderedQueryable<PlatformUser>? o = null;
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
                ("email", true) => asc ? query.OrderBy(a => a.Email) : query.OrderByDescending(a => a.Email),
                ("email", false) => asc ? o!.ThenBy(a => a.Email) : o!.ThenByDescending(a => a.Email),
                ("createdAt", true) => asc ? query.OrderBy(a => a.CreatedAt) : query.OrderByDescending(a => a.CreatedAt),
                ("createdAt", false) => asc ? o!.ThenBy(a => a.CreatedAt) : o!.ThenByDescending(a => a.CreatedAt),
                _ => o,
            };
        }
        // One final id tiebreak closes the surviving chain; the default is newest-first + id (REQ-1.3).
        return o is null
            ? query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.Id)
            : o.ThenBy(a => a.Id);
    }

    /// <summary>Free-text search: escaped substring LIKE over <c>email</c> (the only searchable field, REQ-1.4);
    /// an empty query or a request for only non-whitelisted fields applies no predicate.</summary>
    public static IQueryable<PlatformUser> ApplySearch(this IQueryable<PlatformUser> query, SearchOption? search)
    {
        if (search is null || string.IsNullOrWhiteSpace(search.Query)) return query;

        var fields = (search.Fields is { Length: > 0 } requested ? requested : SearchFields.ToArray())
            .Where(SearchFields.Contains).ToArray();     // silent-drop non-whitelisted requested fields
        if (fields.Length == 0) return query;

        var pattern = $"%{SfsLike.Escape(search.Query.Trim())}%";
        return query.Where(a => EF.Functions.Like(a.Email, pattern, "\\"));
    }

    // tier/status are always lowercase on the wire (no global string-enum converter), so parse here instead of
    // Enum.Parse (case-sensitive on the PascalCase members). A bad value is a 400 (REQ-1.7), not a 409/500.
    private static PlatformUserTier ParseTier(string value) => value.ToLowerInvariant() switch
    {
        "super" => PlatformUserTier.Super,
        "scoped" => PlatformUserTier.Scoped,
        _ => throw new ArgumentException("Invalid admin tier."),
    };

    private static AdminStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "active" => AdminStatus.Active,
        "suspended" => AdminStatus.Suspended,
        _ => throw new ArgumentException("Invalid admin status."),
    };

    // Coerce a JsonElement filter value to string, guarded: a non-string kind raises an ArgumentException (-> 400)
    // instead of letting a raw accessor surface an InvalidOperationException (-> 409) or FormatException (-> 500).
    private static string Str(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } element && element.GetString() is { } s)
            return s;
        throw new ArgumentException("Filter value must be a string.");
    }
}
