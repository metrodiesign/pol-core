using System.Collections.Frozen;
using System.Text.Json;
using BuildingBlocks.Application;
using Merchants.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Persistence.MerchantUsers.Users;

internal static class MerchantUserManagementSfs
{
    private static readonly FrozenSet<string> SortFields =
        new[] { "createdAt", "displayName", "status" }.ToFrozenSet(StringComparer.Ordinal);

    public static IQueryable<User> ApplyFilters(this IQueryable<User> query,
        IReadOnlyList<FilterOption> filters, ILogger? logger = null)
    {
        foreach (var filter in filters)
        {
            switch (filter.Field, filter.Operator)
            {
                case ("status", FilterOperator.Equals):
                    var status = ParseStatus(String(filter.Value));
                    query = query.Where(user => user.Status == status);
                    break;
                case ("status", FilterOperator.In) when filter.Values is { Length: > 0 }:
                    var statuses = filter.Values.Select(value => ParseStatus(String(value))).ToArray();
                    query = query.Where(user => statuses.Contains(user.Status));
                    break;
                case ("displayName", FilterOperator.Contains):
                    var pattern = $"%{SfsLike.Escape(String(filter.Value))}%";
                    query = query.Where(user => EF.Functions.Like(user.DisplayName, pattern, "\\"));
                    break;
                case ("roleCode", FilterOperator.Equals):
                    break; // resolved to role id before this single-context query
                default:
                    logger?.LogDebug("SFS filter dropped: unsupported field/operator {Field}/{Operator}",
                        filter.Field, filter.Operator);
                    break;
            }
        }
        return query;
    }

    public static IQueryable<User> ApplySort(this IQueryable<User> query,
        IReadOnlyList<SortOption> sort, ILogger? logger = null)
    {
        IOrderedQueryable<User>? ordered = null;
        foreach (var option in sort)
        {
            if (!SortFields.Contains(option.Field))
            {
                logger?.LogDebug("SFS sort dropped: unsupported field {Field}", option.Field);
                continue;
            }
            var asc = option.Order == SortDirection.Asc;
            ordered = (option.Field, ordered is null) switch
            {
                ("createdAt", true) => asc ? query.OrderBy(x => x.CreatedAt) : query.OrderByDescending(x => x.CreatedAt),
                ("createdAt", false) => asc ? ordered!.ThenBy(x => x.CreatedAt) : ordered!.ThenByDescending(x => x.CreatedAt),
                ("displayName", true) => asc ? query.OrderBy(x => x.DisplayName) : query.OrderByDescending(x => x.DisplayName),
                ("displayName", false) => asc ? ordered!.ThenBy(x => x.DisplayName) : ordered!.ThenByDescending(x => x.DisplayName),
                ("status", true) => asc ? query.OrderBy(x => x.Status) : query.OrderByDescending(x => x.Status),
                ("status", false) => asc ? ordered!.ThenBy(x => x.Status) : ordered!.ThenByDescending(x => x.Status),
                _ => ordered,
            };
        }
        return ordered is null
            ? query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            : ordered.ThenBy(x => x.Id);
    }

    private static UserStatus ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "pendingapproval" or "pending" => UserStatus.PendingApproval,
        "active" => UserStatus.Active,
        "rejected" => UserStatus.Rejected,
        "suspended" => UserStatus.Suspended,
        _ => throw new ArgumentException("Invalid merchant-user status."),
    };

    private static string String(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.String } element && element.GetString() is { } text
            ? text : throw new ArgumentException("Filter value must be a string.");
}
