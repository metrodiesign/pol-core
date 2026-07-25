using System.Collections.Frozen;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;
using OrderItem = Orders.Domain.Items.Item;

namespace Persistence.MerchantRuntime.Orders.Items;

/// <summary>
/// The SFS apply pipeline + row-to-wire mapping for the policy-reference report (policy-reference-record
/// REQ-4), shared by the merchant-scoped <see cref="PolicyReportRepository"/> and the admin cross-merchant
/// <see cref="AdminItemPolicyReader"/> — both project the SAME <c>OrderItems JOIN Orders LEFT JOIN
/// OrderItemPolicies</c> shape, confined to a merchant set in SQL (<see cref="BuildQuery"/>), differing only in
/// whether it ignores query filters and which merchant set confines it. Mirrors <c>ProductSfs</c>'s pattern
/// (design.md Technology Decision #3 — reuse SFS, don't build a new engine).
/// <para>
/// ponytail: <see cref="ApplyFilters"/>/<see cref="ApplySort"/> run in-memory (LINQ-to-Objects) over the
/// merchant-confined rows, not as further SQL. EF Core (this repo's version, SQLite provider) refused to
/// translate ANY <c>Where</c>/<c>OrderBy</c> chained after a <c>Select</c> into a positional-record type here
/// — tried a plain 3-field wrapper and a ternary-laden one, both failed identically
/// ("could not be translated"). The SQL-side boundary that actually matters for cost (confining rows to one
/// merchant / an admin's accessible set, in <see cref="BuildQuery"/>) still happens in SQL; only the
/// fine-grained SFS filter/sort/paging over that already-bounded set is client-side. Revisit with a flat
/// scalar (non-record) projection if a merchant's row count ever makes the in-memory step measurably slow.
/// </para>
/// </summary>
internal static class PolicyReportSfs
{
    /// <summary>One row per <see cref="OrderItem"/> — <see cref="Policy"/> is null for an item with no
    /// <see cref="ItemPolicy"/> row yet (REQ-1.7, the LEFT JOIN via a correlated subquery).</summary>
    internal sealed record Joined(OrderItem Item, OrderAggregate Order, ItemPolicy? Policy);

    /// <summary>Joins the 3 report tables, confined to <paramref name="confineToMerchants"/> (null = every
    /// merchant, only ever passed by the unrestricted-admin path). <paramref name="ignoreFilters"/> must be
    /// applied to EACH source individually (<c>IgnoreQueryFilters()</c> is a per-query-root operator) —
    /// mirrors <c>AdminItemPolicyWriter</c>'s own per-<c>Set&lt;T&gt;()</c> calls. The confinement Where runs
    /// on the raw entity sets, BEFORE the join/select — this is the part that stays genuine SQL (bounds how
    /// much gets pulled into memory for the in-memory SFS step below).</summary>
    public static IQueryable<Joined> BuildQuery(
        MerchantRuntimeDbContext db, bool ignoreFilters, IReadOnlySet<Guid>? confineToMerchants)
    {
        var items = ignoreFilters ? db.Set<OrderItem>().IgnoreQueryFilters() : db.Set<OrderItem>();
        var orders = ignoreFilters ? db.Set<OrderAggregate>().IgnoreQueryFilters() : db.Set<OrderAggregate>();
        var policies = ignoreFilters ? db.Set<ItemPolicy>().IgnoreQueryFilters() : db.Set<ItemPolicy>();

        if (confineToMerchants is not null)
        {
            items = items.Where(i => confineToMerchants.Contains(i.MerchantId));
            orders = orders.Where(o => confineToMerchants.Contains(o.MerchantId));
            policies = policies.Where(p => confineToMerchants.Contains(p.MerchantId));
        }

        return
            from i in items.AsNoTracking()
            join o in orders.AsNoTracking() on i.OrderId equals o.Id
            select new Joined(i, o, policies.AsNoTracking().FirstOrDefault(p => p.OrderItemId == i.Id));
    }

    private static readonly FrozenDictionary<string, FilterOperator[]> FilterFields =
        new Dictionary<string, FilterOperator[]>(StringComparer.Ordinal)
        {
            ["insuranceCategory"] = [FilterOperator.Equals],
            ["referenceNumberType"] = [FilterOperator.Equals],
            ["premiumRemittanceStatus"] = [FilterOperator.Equals],
            ["paymentStatus"] = [FilterOperator.Equals],
            ["createdAt"] = [FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual,
                             FilterOperator.LessThan, FilterOperator.LessThanOrEqual, FilterOperator.Between],
        }.ToFrozenDictionary(StringComparer.Ordinal);
    // NB: no "merchantId" in the whitelist — admin uses a separate ?merchantId= query param (REQ-4.2, mirrors ProductSfs.cs:32).

    private static readonly FrozenSet<string> SortFields =
        new[] { "insuranceCategory", "referenceNumberType", "premiumRemittanceStatus", "paymentStatus", "createdAt" }
            .ToFrozenSet(StringComparer.Ordinal);

    public static IEnumerable<Joined> ApplyFilters(
        this IEnumerable<Joined> rows, IReadOnlyList<FilterOption> filters, ILogger? logger = null)
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
            rows = ApplyFilter(rows, f);
        }
        return rows;
    }

    private static IEnumerable<Joined> ApplyFilter(IEnumerable<Joined> rows, FilterOption f)
    {
        switch (f.Field, f.Operator)
        {
            // REQ-4.7: no ItemPolicy row coalesces to NotApplicable — matches here too, not just in ToItem.
            case ("insuranceCategory", FilterOperator.Equals):
            { var v = EnumValue<InsuranceCategory>(f.Value); return rows.Where(x => x.Policy?.InsuranceCategory == v); }
            case ("referenceNumberType", FilterOperator.Equals):
            { var v = EnumValue<ReferenceNumberType>(f.Value); return rows.Where(x => x.Policy?.ReferenceNumberType == v); }
            case ("premiumRemittanceStatus", FilterOperator.Equals):
            { var v = EnumValue<PremiumRemittanceStatus>(f.Value); return rows.Where(x => (x.Policy?.PremiumRemittanceStatus ?? PremiumRemittanceStatus.NotApplicable) == v); }
            case ("paymentStatus", FilterOperator.Equals):
            { var v = EnumValue<OrderStatus>(f.Value); return rows.Where(x => x.Order.Status == v); }

            case ("createdAt", FilterOperator.GreaterThan): { var v = Date(f.Value); return rows.Where(x => x.Order.CreatedAt > v); }
            case ("createdAt", FilterOperator.GreaterThanOrEqual): { var v = Date(f.Value); return rows.Where(x => x.Order.CreatedAt >= v); }
            case ("createdAt", FilterOperator.LessThan): { var v = Date(f.Value); return rows.Where(x => x.Order.CreatedAt < v); }
            case ("createdAt", FilterOperator.LessThanOrEqual): { var v = Date(f.Value); return rows.Where(x => x.Order.CreatedAt <= v); }
            case ("createdAt", FilterOperator.Between) when f.Values is { Length: >= 2 }:
            { var lo = Date(f.Values[0]); var hi = Date(f.Values[1]); return rows.Where(x => x.Order.CreatedAt >= lo && x.Order.CreatedAt <= hi); }

            // Reached only when a Between value array is empty/short — silent-drop (mirrors ProductSfs).
            default: return rows;
        }
    }

    public static IEnumerable<Joined> ApplySort(
        this IEnumerable<Joined> rows, IReadOnlyList<SortOption> sort, ILogger? logger = null)
    {
        IOrderedEnumerable<Joined>? o = null;
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
                // insuranceCategory/referenceNumberType are nullable (no ItemPolicy row -> null) -> NULLS-last
                // regardless of direction (design.md "Sort บน nullable column = NULLS-last"). Every other
                // sortable field here is non-nullable (premiumRemittanceStatus coalesced, paymentStatus always set).
                ("insuranceCategory", true) => asc
                    ? rows.OrderBy(x => x.Policy?.InsuranceCategory is null ? 1 : 0).ThenBy(x => x.Policy?.InsuranceCategory)
                    : rows.OrderBy(x => x.Policy?.InsuranceCategory is null ? 1 : 0).ThenByDescending(x => x.Policy?.InsuranceCategory),
                ("insuranceCategory", false) => asc
                    ? o!.ThenBy(x => x.Policy?.InsuranceCategory is null ? 1 : 0).ThenBy(x => x.Policy?.InsuranceCategory)
                    : o!.ThenBy(x => x.Policy?.InsuranceCategory is null ? 1 : 0).ThenByDescending(x => x.Policy?.InsuranceCategory),
                ("referenceNumberType", true) => asc
                    ? rows.OrderBy(x => x.Policy?.ReferenceNumberType is null ? 1 : 0).ThenBy(x => x.Policy?.ReferenceNumberType)
                    : rows.OrderBy(x => x.Policy?.ReferenceNumberType is null ? 1 : 0).ThenByDescending(x => x.Policy?.ReferenceNumberType),
                ("referenceNumberType", false) => asc
                    ? o!.ThenBy(x => x.Policy?.ReferenceNumberType is null ? 1 : 0).ThenBy(x => x.Policy?.ReferenceNumberType)
                    : o!.ThenBy(x => x.Policy?.ReferenceNumberType is null ? 1 : 0).ThenByDescending(x => x.Policy?.ReferenceNumberType),
                ("premiumRemittanceStatus", true) => asc
                    ? rows.OrderBy(x => x.Policy?.PremiumRemittanceStatus ?? PremiumRemittanceStatus.NotApplicable)
                    : rows.OrderByDescending(x => x.Policy?.PremiumRemittanceStatus ?? PremiumRemittanceStatus.NotApplicable),
                ("premiumRemittanceStatus", false) => asc
                    ? o!.ThenBy(x => x.Policy?.PremiumRemittanceStatus ?? PremiumRemittanceStatus.NotApplicable)
                    : o!.ThenByDescending(x => x.Policy?.PremiumRemittanceStatus ?? PremiumRemittanceStatus.NotApplicable),
                ("paymentStatus", true) => asc ? rows.OrderBy(x => x.Order.Status) : rows.OrderByDescending(x => x.Order.Status),
                ("paymentStatus", false) => asc ? o!.ThenBy(x => x.Order.Status) : o!.ThenByDescending(x => x.Order.Status),
                ("createdAt", true) => asc ? rows.OrderBy(x => x.Order.CreatedAt) : rows.OrderByDescending(x => x.Order.CreatedAt),
                ("createdAt", false) => asc ? o!.ThenBy(x => x.Order.CreatedAt) : o!.ThenByDescending(x => x.Order.CreatedAt),
                _ => o,
            };
        }
        // Default fallback (mirrors ProductSfs): newest sale first, ItemId as a tie-breaker so paging is stable.
        return o ?? rows.OrderByDescending(x => x.Order.CreatedAt).ThenByDescending(x => x.Item.Id);
    }

    private static TEnum EnumValue<TEnum>(JsonElement? value) where TEnum : struct, Enum
    {
        if (value is { ValueKind: JsonValueKind.String } element
            && Enum.TryParse<TEnum>(element.GetString(), ignoreCase: true, out var parsed))
            return parsed;
        throw new ArgumentException($"Filter value must be a valid {typeof(TEnum).Name}.");
    }

    private static DateTime Date(JsonElement? value)
    {
        if (value is { ValueKind: JsonValueKind.String } element && element.TryGetDateTime(out var d)) return d;
        throw new ArgumentException("Filter value must be a date.");
    }

    /// <summary>Row -> wire mapping (masking + payment-status label — neither is SQL-translatable anyway).
    /// <paramref name="includeMerchantId"/> is false on the merchant plane (REQ-4.2).</summary>
    public static PolicyReportItem ToItem(this Joined joined, bool includeMerchantId)
    {
        var (item, order, policy) = joined;
        return new PolicyReportItem(
            item.OrderId, item.Id,
            $"{item.InsuredFirstName} {item.InsuredLastName}",
            MaskIdNumber(item.InsuredIdNumber),
            policy?.InsuranceCategory, policy?.ReferenceNumberType,
            policy?.ReferenceNumber, policy?.EndorsementNumber, policy?.RenewalReminderNumber, policy?.InsuredObjectReference,
            policy?.NetPremium, policy?.GrossPremium,
            policy?.PremiumRemittanceStatus ?? PremiumRemittanceStatus.NotApplicable, policy?.DeductedAt,
            PaymentStatusLabel(order.Status),
            includeMerchantId ? item.MerchantId : null);
    }

    // Local to this report's projection, deliberately not shared with Orders.Application.GetOrders' own copy
    // or reused as a cross-file utility (design.md m8) — masked-list, no reveal-audit written for this read.
    private static string MaskIdNumber(string idNumber) =>
        idNumber.Length <= 4 ? new string('*', idNumber.Length) : $"****{idNumber[^4..]}";

    // REQ-4.3 — derived from Order.Status, never a stored/per-item field; never blank (REQ-4.7).
    private static string PaymentStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.AwaitingPayment => "รอชำระเงิน",
        OrderStatus.Paid => "ชำระสำเร็จ",
        OrderStatus.Cancelled => "ยกเลิก",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown OrderStatus."),
    };
}
