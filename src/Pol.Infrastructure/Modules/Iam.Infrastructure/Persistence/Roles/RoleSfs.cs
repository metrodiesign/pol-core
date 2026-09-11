using System.Collections.Frozen;
using System.Text.Json;
using BuildingBlocks.Application;
using Iam.Domain.Roles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SearchOption = BuildingBlocks.Application.SearchOption;   // disambiguate from System.IO.SearchOption

namespace Iam.Infrastructure.Persistence.Roles;

/// <summary>
/// The SFS apply pipeline for the admin-side Role list (REQ-6.1) — moved from
/// <c>Admins.Infrastructure.Persistence.Roles.RoleSfs</c> onto the unified <see cref="Role"/>. Visibility
/// (<see cref="RoleVisibility"/>) is applied by the caller BEFORE this pipeline runs; SFS itself only
/// filters/sorts/searches within whatever set it is handed. Same deny-by-default whitelist shape as every
/// other SFS exemplar in this codebase (see <c>docs/reference/search-filter-sort.md</c>).
/// </summary>
public static class RoleSfs
{
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

    public static IQueryable<Role> ApplyFilters(
        this IQueryable<Role> query, IReadOnlyList<FilterOption> filters, ILogger? logger = null)
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

    private static IQueryable<Role> ApplyFilter(IQueryable<Role> q, FilterOption f)
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

            default: return q; // reached only when a set/range value is empty (the `when` guards above failed)
        }
    }

    public static IQueryable<Role> ApplySort(
        this IQueryable<Role> query, IReadOnlyList<SortOption> sort, ILogger? logger = null)
    {
        IOrderedQueryable<Role>? o = null;
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
                ("description", true) => asc ? query.OrderBy(r => r.Description == null).ThenBy(r => r.Description)
                                             : query.OrderBy(r => r.Description == null).ThenByDescending(r => r.Description),
                ("description", false) => asc ? o!.ThenBy(r => r.Description == null).ThenBy(r => r.Description)
                                              : o!.ThenBy(r => r.Description == null).ThenByDescending(r => r.Description),

                ("name", true) => asc ? query.OrderBy(r => r.Name) : query.OrderByDescending(r => r.Name),
                ("name", false) => asc ? o!.ThenBy(r => r.Name) : o!.ThenByDescending(r => r.Name),
                ("code", true) => asc ? query.OrderBy(r => r.Code) : query.OrderByDescending(r => r.Code),
                ("code", false) => asc ? o!.ThenBy(r => r.Code) : o!.ThenByDescending(r => r.Code),

                _ => o,
            };
        }
        return o ?? query.OrderByDescending(r => r.Code); // mandatory default — Role has no CreatedAt
    }

    public static IQueryable<Role> ApplySearch(this IQueryable<Role> query, SearchOption? search)
    {
        if (search is null || string.IsNullOrWhiteSpace(search.Query)) return query;

        var fields = (search.Fields is { Length: > 0 } requested ? requested : SearchFields.ToArray())
            .Where(SearchFields.Contains).ToArray();
        if (fields.Length == 0) return query;

        var pattern = $"%{SfsLike.Escape(search.Query.Trim())}%";
        bool code = fields.Contains("code"), name = fields.Contains("name"), description = fields.Contains("description");

        return query.Where(r =>
            (code && EF.Functions.Like(r.Code, pattern, "\\")) ||
            (name && EF.Functions.Like(r.Name, pattern, "\\")) ||
            (description && r.Description != null && EF.Functions.Like(r.Description, pattern, "\\")));
    }

    // Role status is always lowercase on the wire; parse it here instead of Enum.Parse (case-sensitive on the
    // PascalCase members). Evaluates client-side to a constant EF parameter. A bad value is a 400, not 409/500.
    private static RoleStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "active" => RoleStatus.Active,
        "inactive" => RoleStatus.Inactive,
        _ => throw new ArgumentException("Invalid role status."),
    };

    private static string Str(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } element && element.GetString() is { } s)
            return s;
        throw new ArgumentException("Filter value must be a string.");
    }
}
