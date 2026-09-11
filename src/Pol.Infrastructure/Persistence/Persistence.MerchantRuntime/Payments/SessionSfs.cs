using System.Collections.Frozen;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Payments.Domain;
using Payments.Domain.Psp;

namespace Persistence.MerchantRuntime.Payments;

internal static class SessionSfs
{
    private static readonly FrozenDictionary<string, FilterOperator[]> FilterFields =
        new Dictionary<string, FilterOperator[]>(StringComparer.Ordinal)
        {
            ["status"] = [FilterOperator.Equals, FilterOperator.In],
            ["method"] = [FilterOperator.Equals, FilterOperator.In],
            ["psp"] = [FilterOperator.Equals, FilterOperator.In],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SortFields =
        new[] { "createdAt", "updatedAt" }.ToFrozenSet(StringComparer.Ordinal);

    public static IQueryable<Session> ApplyFilters(
        this IQueryable<Session> query,
        IReadOnlyList<FilterOption> filters,
        ILogger? logger = null)
    {
        foreach (var filter in filters)
        {
            if (!FilterFields.TryGetValue(filter.Field, out var operators))
            {
                logger?.LogDebug("SFS filter dropped: unknown field {Field}", filter.Field);
                continue;
            }
            if (!operators.Contains(filter.Operator))
            {
                logger?.LogDebug(
                    "SFS filter dropped: operator {Operator} not allowed on field {Field}",
                    filter.Operator,
                    filter.Field);
                continue;
            }

            switch (filter.Field, filter.Operator)
            {
                case ("status", FilterOperator.Equals):
                {
                    var value = Status(String(filter.Value));
                    query = query.Where(x => x.Status == value);
                    break;
                }
                case ("status", FilterOperator.In) when filter.Values is { Length: > 0 }:
                {
                    var values = filter.Values.Select(v => Status(String(v))).ToArray();
                    query = query.Where(x => values.Contains(x.Status));
                    break;
                }
                case ("method", FilterOperator.Equals):
                {
                    var value = Method(String(filter.Value));
                    query = query.Where(x => x.Method == value);
                    break;
                }
                case ("method", FilterOperator.In) when filter.Values is { Length: > 0 }:
                {
                    var values = filter.Values.Select(v => Method(String(v))).ToArray();
                    query = query.Where(x => values.Contains(x.Method));
                    break;
                }
                case ("psp", FilterOperator.Equals):
                {
                    var value = Psp(String(filter.Value));
                    query = query.Where(x => x.Psp == value);
                    break;
                }
                case ("psp", FilterOperator.In) when filter.Values is { Length: > 0 }:
                {
                    var values = filter.Values.Select(v => Psp(String(v))).ToArray();
                    query = query.Where(x => values.Contains(x.Psp));
                    break;
                }
            }
        }

        return query;
    }

    public static IQueryable<Session> ApplySort(
        this IQueryable<Session> query,
        IReadOnlyList<SortOption> sort,
        ILogger? logger = null)
    {
        IOrderedQueryable<Session>? ordered = null;
        foreach (var option in sort)
        {
            if (!SortFields.Contains(option.Field))
            {
                logger?.LogDebug("SFS sort dropped: unknown field {Field}", option.Field);
                continue;
            }

            var ascending = option.Order == SortDirection.Asc;
            ordered = (option.Field, ordered is null) switch
            {
                ("createdAt", true) => ascending
                    ? query.OrderBy(x => x.CreatedAt)
                    : query.OrderByDescending(x => x.CreatedAt),
                ("createdAt", false) => ascending
                    ? ordered!.ThenBy(x => x.CreatedAt)
                    : ordered!.ThenByDescending(x => x.CreatedAt),
                ("updatedAt", true) => ascending
                    ? query.OrderBy(x => x.UpdatedAt)
                    : query.OrderByDescending(x => x.UpdatedAt),
                ("updatedAt", false) => ascending
                    ? ordered!.ThenBy(x => x.UpdatedAt)
                    : ordered!.ThenByDescending(x => x.UpdatedAt),
                _ => ordered,
            };
        }

        return ordered is null
            ? query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            : ordered.ThenBy(x => x.Id);
    }

    private static string String(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } element && element.GetString() is { } text)
            return text;
        throw new ArgumentException("Filter value must be a string.");
    }

    private static SessionStatus Status(string value) =>
        Enum.TryParse<SessionStatus>(value, ignoreCase: true, out var status)
            ? status
            : throw new ArgumentException("Invalid payment-session status.");

    private static string Method(string value) => value.Trim().ToLowerInvariant() switch
    {
        "card" => "card",
        "promptpay" => "promptpay",
        "installment" => "installment",
        _ => throw new ArgumentException("Invalid payment method."),
    };

    private static Code Psp(string value)
    {
        try { return Codes.FromCode(value.Trim().ToLowerInvariant()); }
        catch (ArgumentOutOfRangeException ex) { throw new ArgumentException("Invalid PSP code.", ex); }
    }
}
