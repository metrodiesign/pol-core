using System.Collections.Frozen;
using System.Text.Json;
using Admin.Domain;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SearchOption = BuildingBlocks.Application.SearchOption;   // disambiguate from System.IO.SearchOption

namespace Admin.Infrastructure.Persistence;

/// <summary>
/// The SFS apply pipeline for the AdminRole list — the control-plane exemplar. Deny-by-default whitelists
/// (immutable <see cref="FrozenDictionary{TKey,TValue}"/> / <see cref="FrozenSet{T}"/>) gate which
/// field+operator pairs reach SQL; every predicate is a compile-checked, parameterized EF Core
/// <c>.Where</c>/ordering, so no client string is ever interpolated into SQL. Filter values are coerced from
/// <see cref="JsonElement"/> eagerly and guarded, so a type mismatch is a 400 (<see cref="ArgumentException"/>)
/// rather than a 409/500 from a raw accessor. NULLS-last is emulated inline for the nullable Description
/// column, and a mandatory default sort keeps paging deterministic.
///
/// AdminRole is all string/enum columns, so this exemplar exercises the 9 operators meaningful to those
/// columns (eq, ne, in, not_in, like, ilike, contains, is_null, is_not_null). The 5 range/numeric operators
/// (gt, gte, lt, lte, between) are demonstrated on Products in the merchant-scoped exemplar, where numeric/date
/// columns exist; the full 14-operator reference is doc section 4.1. (REQ-3, REQ-4, REQ-5, REQ-6, REQ-8.5, REQ-8.6)
/// </summary>
public static class AdminRoleSfs
{
    // Deny-by-default filter whitelist. Matched case-sensitively (Ordinal) so a wrong-case field is treated as
    // absent (REQ-6.7). Each field maps to exactly the operators allowed on it (REQ-3.1).
    private static readonly FrozenDictionary<string, FilterOperator[]> FilterFields =
        new Dictionary<string, FilterOperator[]>(StringComparer.Ordinal)
        {
            ["status"] = [FilterOperator.Equals, FilterOperator.In],
            ["code"] = [FilterOperator.Equals, FilterOperator.NotEquals, FilterOperator.In, FilterOperator.NotIn,
                        FilterOperator.Like, FilterOperator.ILike, FilterOperator.Contains],
            ["name"] = [FilterOperator.Equals, FilterOperator.NotEquals,
                        FilterOperator.Like, FilterOperator.ILike, FilterOperator.Contains],
            ["description"] = [FilterOperator.IsNull, FilterOperator.IsNotNull,
                               FilterOperator.Like, FilterOperator.ILike, FilterOperator.Contains],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SortFields =
        new[] { "code", "name", "description" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SearchFields =
        new[] { "code", "name", "description" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Applies the whitelisted filters as AND-combined parameterized predicates (REQ-3.2, REQ-3.7).
    /// Unknown fields, disallowed operators, and empty set/range values are silently dropped (REQ-3.3, REQ-3.4,
    /// REQ-3.6) and logged by name at debug level (REQ-8.6).</summary>
    public static IQueryable<AdminRole> ApplyFilters(
        this IQueryable<AdminRole> query, IReadOnlyList<FilterOption> filters, ILogger? logger = null)
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

    private static IQueryable<AdminRole> ApplyFilter(IQueryable<AdminRole> q, FilterOption f)
    {
        switch (f.Field, f.Operator)
        {
            case ("status", FilterOperator.Equals):
            {
                var s = ParseStatus(Str(f.Value));
                return q.Where(r => r.Status == s);
            }
            case ("status", FilterOperator.In) when f.Values is { Length: > 0 }:
            {
                var set = f.Values.Select(v => ParseStatus(Str(v))).ToArray();
                return q.Where(r => set.Contains(r.Status));
            }

            case ("code", FilterOperator.Equals): { var v = Str(f.Value); return q.Where(r => r.Code == v); }
            case ("code", FilterOperator.NotEquals): { var v = Str(f.Value); return q.Where(r => r.Code != v); }
            case ("code", FilterOperator.In) when f.Values is { Length: > 0 }:
            { var vs = f.Values.Select(v => Str(v)).ToArray(); return q.Where(r => vs.Contains(r.Code)); }
            case ("code", FilterOperator.NotIn) when f.Values is { Length: > 0 }:
            { var vs = f.Values.Select(v => Str(v)).ToArray(); return q.Where(r => !vs.Contains(r.Code)); }
            case ("code", FilterOperator.Like):
            case ("code", FilterOperator.ILike):
            { var p = SfsLike.Escape(Str(f.Value)); return q.Where(r => EF.Functions.Like(r.Code, p, "\\")); }
            case ("code", FilterOperator.Contains):
            { var p = $"%{SfsLike.Escape(Str(f.Value))}%"; return q.Where(r => EF.Functions.Like(r.Code, p, "\\")); }

            case ("name", FilterOperator.Equals): { var v = Str(f.Value); return q.Where(r => r.Name == v); }
            case ("name", FilterOperator.NotEquals): { var v = Str(f.Value); return q.Where(r => r.Name != v); }
            case ("name", FilterOperator.Like):
            case ("name", FilterOperator.ILike):
            { var p = SfsLike.Escape(Str(f.Value)); return q.Where(r => EF.Functions.Like(r.Name, p, "\\")); }
            case ("name", FilterOperator.Contains):
            { var p = $"%{SfsLike.Escape(Str(f.Value))}%"; return q.Where(r => EF.Functions.Like(r.Name, p, "\\")); }

            case ("description", FilterOperator.IsNull): return q.Where(r => r.Description == null);
            case ("description", FilterOperator.IsNotNull): return q.Where(r => r.Description != null);
            case ("description", FilterOperator.Like):
            case ("description", FilterOperator.ILike):
            { var p = SfsLike.Escape(Str(f.Value)); return q.Where(r => r.Description != null && EF.Functions.Like(r.Description, p, "\\")); }
            case ("description", FilterOperator.Contains):
            { var p = $"%{SfsLike.Escape(Str(f.Value))}%"; return q.Where(r => r.Description != null && EF.Functions.Like(r.Description, p, "\\")); }

            // Reached only when a set/range value is empty (the `when` guards above failed) — silent-drop (REQ-3.6).
            default: return q;
        }
    }

    /// <summary>Orders by the whitelisted sort keys in the order given (REQ-4.2), NULLS-last on the nullable
    /// Description column in both directions (REQ-4.4), with a mandatory deterministic default so paging is
    /// stable when no key survives the whitelist (REQ-4.5). Sort fields map to properties via compile-checked
    /// code — no client string reaches ORDER BY (REQ-4.6).</summary>
    public static IQueryable<AdminRole> ApplySort(
        this IQueryable<AdminRole> query, IReadOnlyList<SortOption> sort, ILogger? logger = null)
    {
        IOrderedQueryable<AdminRole>? o = null;
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
                // description = nullable -> NULLS LAST: order (Description == null) first, then Description.
                ("description", true) => asc ? query.OrderBy(r => r.Description == null).ThenBy(r => r.Description)
                                             : query.OrderBy(r => r.Description == null).ThenByDescending(r => r.Description),
                ("description", false) => asc ? o!.ThenBy(r => r.Description == null).ThenBy(r => r.Description)
                                              : o!.ThenBy(r => r.Description == null).ThenByDescending(r => r.Description),

                // code / name = non-nullable -> plain ordering.
                ("name", true) => asc ? query.OrderBy(r => r.Name) : query.OrderByDescending(r => r.Name),
                ("name", false) => asc ? o!.ThenBy(r => r.Name) : o!.ThenByDescending(r => r.Name),
                ("code", true) => asc ? query.OrderBy(r => r.Code) : query.OrderByDescending(r => r.Code),
                ("code", false) => asc ? o!.ThenBy(r => r.Code) : o!.ThenByDescending(r => r.Code),

                _ => o,
            };
        }
        return o ?? query.OrderByDescending(r => r.Code);   // mandatory default — AdminRole has no CreatedAt (REQ-4.5)
    }

    /// <summary>Free-text search: case-insensitive substring (CI collation), OR-combined across the intersection
    /// of requested and whitelisted fields, defaulting to all whitelisted fields (REQ-5.2, REQ-5.3). The term is
    /// LIKE-escaped with an explicit ESCAPE clause so wildcard characters match literally (REQ-5.4); an empty
    /// query applies no predicate (REQ-5.5).</summary>
    public static IQueryable<AdminRole> ApplySearch(this IQueryable<AdminRole> query, SearchOption? search)
    {
        if (search is null || string.IsNullOrWhiteSpace(search.Query)) return query;

        var fields = (search.Fields is { Length: > 0 } requested ? requested : SearchFields.ToArray())
            .Where(SearchFields.Contains).ToArray();     // silent-drop non-whitelisted requested fields
        if (fields.Length == 0) return query;

        var pattern = $"%{SfsLike.Escape(search.Query.Trim())}%";
        bool code = fields.Contains("code"), name = fields.Contains("name"), description = fields.Contains("description");

        // The bool flags are constants at translation time, so EF prunes the un-requested branches; the single
        // grouped OR keeps LIKE precedence correct without an outer AND leaking in.
        return query.Where(r =>
            (code && EF.Functions.Like(r.Code, pattern, "\\")) ||
            (name && EF.Functions.Like(r.Name, pattern, "\\")) ||
            (description && r.Description != null && EF.Functions.Like(r.Description, pattern, "\\")));
    }

    // Role status is always lowercase on the wire (the host has no global string-enum converter), so parse it
    // here instead of Enum.Parse (case-sensitive on the PascalCase members). Evaluates client-side to a constant
    // EF parameter. A bad value is a 400, not a 409/500.
    private static AdminRoleStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "active" => AdminRoleStatus.Active,
        "inactive" => AdminRoleStatus.Inactive,
        _ => throw new ArgumentException("Invalid role status."),
    };

    // Coerce a JsonElement filter value to string, guarded: a non-string kind (e.g. a JSON number) raises an
    // ArgumentException (-> 400) instead of letting the raw JsonElement accessor surface an
    // InvalidOperationException (-> 409) or FormatException (-> 500). (REQ-8.5)
    private static string Str(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } element && element.GetString() is { } s)
            return s;
        throw new ArgumentException("Filter value must be a string.");
    }
}
