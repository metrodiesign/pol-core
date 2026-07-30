using System.Collections.Frozen;
using System.Text.Json;
using BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Products.Domain;
using SearchOption = BuildingBlocks.Application.SearchOption;   // disambiguate from System.IO.SearchOption

namespace Persistence.MerchantRuntime.Products;

/// <summary>
/// The SFS apply pipeline for the merchant-scoped insurance-document list. Whitelists follow the
/// VCentralPay SP guide §2 vocabulary. No whitelist exposes <c>merchantId</c> or any cross-aggregate
/// key, so SFS can only narrow within the merchant floor, never widen it (REQ-7.3). Filter values are
/// coerced from <see cref="JsonElement"/> eagerly and guarded, so a type mismatch is a 400
/// (<see cref="ArgumentException"/>), never a 409/500.
/// </summary>
internal static class ProductSfs
{
    private static readonly FilterOperator[] RangeOperators =
        [FilterOperator.Equals, FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual,
         FilterOperator.LessThan, FilterOperator.LessThanOrEqual, FilterOperator.Between];

    private static readonly FilterOperator[] DateOperators =
        [FilterOperator.GreaterThan, FilterOperator.GreaterThanOrEqual,
         FilterOperator.LessThan, FilterOperator.LessThanOrEqual, FilterOperator.Between];

    private static readonly FrozenDictionary<string, FilterOperator[]> FilterFields =
        new Dictionary<string, FilterOperator[]>(StringComparer.Ordinal)
        {
            ["isActive"] = [FilterOperator.Equals],
            ["paymentStatus"] = [FilterOperator.Equals],
            ["documentType"] = [FilterOperator.Equals],
            ["productGroup"] = [FilterOperator.Equals],
            ["totalPremiumAmount"] = RangeOperators,
            ["startDate"] = DateOperators,
            ["endDate"] = DateOperators,
            ["paidDate"] = DateOperators,
            ["createdAt"] = DateOperators,
        }.ToFrozenDictionary(StringComparer.Ordinal);
    // NB: no "merchantId" (or any cross-aggregate FK) in any whitelist — SFS must never widen merchant scope (REQ-7.3).

    private static readonly FrozenSet<string> SortFields =
        new[] { "documentNo", "totalPremiumAmount", "startDate", "paidDate", "createdAt" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> SearchFields =
        new[] { "documentNo", "showName", "policyNumber", "applicationNumber", "licensePlateNumber" }
            .ToFrozenSet(StringComparer.Ordinal);

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
            case ("paymentStatus", FilterOperator.Equals): { var v = Enum<PaymentStatus>(f.Value); return q.Where(p => p.PaymentStatus == v); }
            case ("documentType", FilterOperator.Equals): { var v = Enum<DocumentType>(f.Value); return q.Where(p => p.DocumentType == v); }
            case ("productGroup", FilterOperator.Equals): { var v = Enum<ProductGroup>(f.Value); return q.Where(p => p.ProductGroup == v); }

            case ("totalPremiumAmount", FilterOperator.Equals): { var v = Decimal(f.Value); return q.Where(p => p.TotalPremium.Amount == v); }
            case ("totalPremiumAmount", FilterOperator.GreaterThan): { var v = Decimal(f.Value); return q.Where(p => p.TotalPremium.Amount > v); }
            case ("totalPremiumAmount", FilterOperator.GreaterThanOrEqual): { var v = Decimal(f.Value); return q.Where(p => p.TotalPremium.Amount >= v); }
            case ("totalPremiumAmount", FilterOperator.LessThan): { var v = Decimal(f.Value); return q.Where(p => p.TotalPremium.Amount < v); }
            case ("totalPremiumAmount", FilterOperator.LessThanOrEqual): { var v = Decimal(f.Value); return q.Where(p => p.TotalPremium.Amount <= v); }
            case ("totalPremiumAmount", FilterOperator.Between) when f.Values is { Length: >= 2 }:
            { var lo = Decimal(f.Values[0]); var hi = Decimal(f.Values[1]); return q.Where(p => p.TotalPremium.Amount >= lo && p.TotalPremium.Amount <= hi); }

            case ("startDate", FilterOperator.GreaterThan): { var v = Date(f.Value); return q.Where(p => p.StartDate > v); }
            case ("startDate", FilterOperator.GreaterThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.StartDate >= v); }
            case ("startDate", FilterOperator.LessThan): { var v = Date(f.Value); return q.Where(p => p.StartDate < v); }
            case ("startDate", FilterOperator.LessThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.StartDate <= v); }
            case ("startDate", FilterOperator.Between) when f.Values is { Length: >= 2 }:
            { var lo = Date(f.Values[0]); var hi = Date(f.Values[1]); return q.Where(p => p.StartDate >= lo && p.StartDate <= hi); }

            case ("endDate", FilterOperator.GreaterThan): { var v = Date(f.Value); return q.Where(p => p.EndDate > v); }
            case ("endDate", FilterOperator.GreaterThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.EndDate >= v); }
            case ("endDate", FilterOperator.LessThan): { var v = Date(f.Value); return q.Where(p => p.EndDate < v); }
            case ("endDate", FilterOperator.LessThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.EndDate <= v); }
            case ("endDate", FilterOperator.Between) when f.Values is { Length: >= 2 }:
            { var lo = Date(f.Values[0]); var hi = Date(f.Values[1]); return q.Where(p => p.EndDate >= lo && p.EndDate <= hi); }

            case ("paidDate", FilterOperator.GreaterThan): { var v = Date(f.Value); return q.Where(p => p.PaidDate > v); }
            case ("paidDate", FilterOperator.GreaterThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.PaidDate >= v); }
            case ("paidDate", FilterOperator.LessThan): { var v = Date(f.Value); return q.Where(p => p.PaidDate < v); }
            case ("paidDate", FilterOperator.LessThanOrEqual): { var v = Date(f.Value); return q.Where(p => p.PaidDate <= v); }
            case ("paidDate", FilterOperator.Between) when f.Values is { Length: >= 2 }:
            { var lo = Date(f.Values[0]); var hi = Date(f.Values[1]); return q.Where(p => p.PaidDate >= lo && p.PaidDate <= hi); }

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
                ("documentNo", true) => asc ? query.OrderBy(p => p.DocumentNo) : query.OrderByDescending(p => p.DocumentNo),
                ("documentNo", false) => asc ? o!.ThenBy(p => p.DocumentNo) : o!.ThenByDescending(p => p.DocumentNo),
                ("totalPremiumAmount", true) => asc ? query.OrderBy(p => p.TotalPremium.Amount) : query.OrderByDescending(p => p.TotalPremium.Amount),
                ("totalPremiumAmount", false) => asc ? o!.ThenBy(p => p.TotalPremium.Amount) : o!.ThenByDescending(p => p.TotalPremium.Amount),
                ("startDate", true) => asc ? query.OrderBy(p => p.StartDate) : query.OrderByDescending(p => p.StartDate),
                ("startDate", false) => asc ? o!.ThenBy(p => p.StartDate) : o!.ThenByDescending(p => p.StartDate),
                ("paidDate", true) => asc ? query.OrderBy(p => p.PaidDate) : query.OrderByDescending(p => p.PaidDate),
                ("paidDate", false) => asc ? o!.ThenBy(p => p.PaidDate) : o!.ThenByDescending(p => p.PaidDate),
                ("createdAt", true) => asc ? query.OrderBy(p => p.CreatedAt) : query.OrderByDescending(p => p.CreatedAt),
                ("createdAt", false) => asc ? o!.ThenBy(p => p.CreatedAt) : o!.ThenByDescending(p => p.CreatedAt),
                _ => o,
            };
        }
        // Default fallback. CreatedAt is not unique (bulk/seed ties), so append the unique Id as a
        // tie-breaker — without it, tied timestamps let SQL Server order rows arbitrarily and paging can
        // duplicate/skip items across pages.
        return o ?? query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id);
    }

    public static IQueryable<Product> ApplySearch(this IQueryable<Product> query, SearchOption? search)
    {
        if (search is null || string.IsNullOrWhiteSpace(search.Query)) return query;

        var fields = (search.Fields is { Length: > 0 } requested ? requested : SearchFields.ToArray())
            .Where(SearchFields.Contains).ToArray();
        if (fields.Length == 0) return query;

        var pattern = $"%{SfsLike.Escape(search.Query.Trim())}%";
        bool inDocumentNo = fields.Contains("documentNo");
        bool inShowName = fields.Contains("showName");
        bool inPolicyNumber = fields.Contains("policyNumber");
        bool inApplicationNumber = fields.Contains("applicationNumber");
        bool inLicensePlateNumber = fields.Contains("licensePlateNumber");

        return query.Where(p =>
            (inDocumentNo && EF.Functions.Like(p.DocumentNo, pattern, "\\"))
            || (inShowName && p.ShowName != null && EF.Functions.Like(p.ShowName, pattern, "\\"))
            || (inPolicyNumber && p.PolicyNumber != null && EF.Functions.Like(p.PolicyNumber, pattern, "\\"))
            || (inApplicationNumber && p.ApplicationNumber != null && EF.Functions.Like(p.ApplicationNumber, pattern, "\\"))
            || (inLicensePlateNumber && p.LicensePlateNumber != null && EF.Functions.Like(p.LicensePlateNumber, pattern, "\\")));
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

    private static TEnum Enum<TEnum>(JsonElement? value) where TEnum : struct, System.Enum
    {
        if (value is { ValueKind: JsonValueKind.String } element
            && System.Enum.TryParse<TEnum>(element.GetString(), ignoreCase: true, out var parsed))
            return parsed;
        throw new ArgumentException($"Filter value must be one of: {string.Join(", ", System.Enum.GetNames<TEnum>())}.");
    }
}
