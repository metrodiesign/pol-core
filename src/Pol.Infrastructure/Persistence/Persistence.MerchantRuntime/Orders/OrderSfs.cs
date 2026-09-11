using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Domain;

namespace Persistence.MerchantRuntime.Orders;

internal static class OrderSfs
{
    private static readonly FrozenDictionary<string, FilterOperator[]> FilterFields =
        new Dictionary<string, FilterOperator[]>(StringComparer.Ordinal)
        {
            ["orderNo"] = [FilterOperator.Equals, FilterOperator.Contains],
            ["status"] = [FilterOperator.Equals, FilterOperator.In],
            ["paymentChannel"] = [FilterOperator.Equals, FilterOperator.In],
            ["merchantId"] = [FilterOperator.Equals, FilterOperator.In],
            ["originatorId"] = [FilterOperator.Equals, FilterOperator.In, FilterOperator.IsNull],
            ["createdAt"] = [FilterOperator.GreaterThanOrEqual, FilterOperator.LessThanOrEqual, FilterOperator.Between],
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SortFields =
        new[] { "createdAt", "orderNo" }.ToFrozenSet(StringComparer.Ordinal);

    public static IQueryable<Order> ApplyFilters(
        this IQueryable<Order> query,
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

            query = ApplyFilter(query, filter);
        }

        return query;
    }

    private static IQueryable<Order> ApplyFilter(IQueryable<Order> query, FilterOption filter)
    {
        switch (filter.Field, filter.Operator)
        {
            case ("orderNo", FilterOperator.Equals):
                {
                    var value = String(filter.Value);
                    return query.Where(x => x.OrderNo == value);
                }
            case ("orderNo", FilterOperator.Contains):
                {
                    var pattern = $"%{SfsLike.Escape(String(filter.Value))}%";
                    return query.Where(x => EF.Functions.Like(x.OrderNo, pattern, "\\"));
                }
            case ("status", FilterOperator.Equals):
                {
                    var value = Status(String(filter.Value));
                    return query.Where(x => x.Status == value);
                }
            case ("status", FilterOperator.In) when filter.Values is { Length: > 0 }:
                {
                    var values = filter.Values.Select(v => Status(String(v))).ToArray();
                    return query.Where(x => values.Contains(x.Status));
                }
            case ("paymentChannel", FilterOperator.Equals):
                {
                    var value = PaymentMethod(String(filter.Value));
                    return query.Where(x => x.PaymentChannel == value);
                }
            case ("paymentChannel", FilterOperator.In) when filter.Values is { Length: > 0 }:
                {
                    var values = filter.Values.Select(v => PaymentMethod(String(v))).ToArray();
                    return query.Where(x => values.Contains(x.PaymentChannel!));
                }
            case ("merchantId", FilterOperator.Equals):
                {
                    var value = GuidValue(filter.Value);
                    return query.Where(x => x.MerchantId == value);
                }
            case ("merchantId", FilterOperator.In) when filter.Values is { Length: > 0 }:
                {
                    var values = filter.Values.Select(value => GuidValue(value)).ToArray();
                    return query.Where(x => values.Contains(x.MerchantId));
                }
            case ("originatorId", FilterOperator.Equals):
                {
                    var value = GuidValue(filter.Value);
                    return query.Where(x => x.OriginatorId == value);
                }
            case ("originatorId", FilterOperator.In) when filter.Values is { Length: > 0 }:
                {
                    var values = filter.Values.Select(value => GuidValue(value)).ToArray();
                    return query.Where(x => x.OriginatorId != null && values.Contains(x.OriginatorId.Value));
                }
            case ("originatorId", FilterOperator.IsNull):
                return query.Where(x => x.OriginatorId == null);
            case ("createdAt", FilterOperator.GreaterThanOrEqual):
                {
                    var value = Instant(filter.Value);
                    return query.Where(x => x.CreatedAt >= value);
                }
            case ("createdAt", FilterOperator.LessThanOrEqual):
                {
                    var value = Instant(filter.Value);
                    return query.Where(x => x.CreatedAt <= value);
                }
            case ("createdAt", FilterOperator.Between) when filter.Values is { Length: 2 }:
                {
                    var start = Instant(filter.Values[0]);
                    var end = Instant(filter.Values[1]);
                    if (start > end)
                        throw new ArgumentException("createdAt range is reversed.");
                    return query.Where(x => x.CreatedAt >= start && x.CreatedAt <= end);
                }
            default:
                return query;
        }
    }

    public static IQueryable<Order> ApplySearch(
        this IQueryable<Order> query,
        BuildingBlocks.Application.SearchOption? search)
    {
        if (string.IsNullOrWhiteSpace(search?.Query))
            return query;
        var pattern = $"%{SfsLike.Escape(search.Query.Trim())}%";
        return query.Where(x =>
            EF.Functions.Like(x.OrderNo, pattern, "\\")
            || EF.Functions.Like(x.CustomerName, pattern, "\\")
            || (x.CustomerEmail != null && EF.Functions.Like(x.CustomerEmail, pattern, "\\")));
    }

    public static IQueryable<Order> ApplySort(
        this IQueryable<Order> query,
        IReadOnlyList<SortOption> sort,
        ILogger? logger = null)
    {
        IOrderedQueryable<Order>? ordered = null;
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
                ("orderNo", true) => ascending
                    ? query.OrderBy(x => x.OrderNo)
                    : query.OrderByDescending(x => x.OrderNo),
                ("orderNo", false) => ascending
                    ? ordered!.ThenBy(x => x.OrderNo)
                    : ordered!.ThenByDescending(x => x.OrderNo),
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

    private static Guid GuidValue(JsonElement? value) =>
        Guid.TryParse(String(value), out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new ArgumentException("Filter value must be a non-empty UUID.");

    private static DateTime Instant(JsonElement? value) =>
        DateTime.TryParse(String(value), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : throw new ArgumentException("Filter value must be an ISO-8601 instant.");

    private static OrderStatus Status(string value) =>
        Enum.TryParse<OrderStatus>(value, ignoreCase: true, out var status)
            ? status
            : throw new ArgumentException("Invalid order status.");

    private static string PaymentMethod(string value) => value.Trim().ToLowerInvariant() switch
    {
        "card" => "card",
        "promptpay" => "promptpay",
        "installment" => "installment",
        _ => throw new ArgumentException("Invalid payment channel."),
    };
}
