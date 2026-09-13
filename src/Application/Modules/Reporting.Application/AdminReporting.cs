using BuildingBlocks.Application;

namespace Reporting.Application;

public sealed record ReportingAccess(bool IsUnrestricted, IReadOnlySet<Guid> MerchantIds)
{
    public bool Allows(Guid merchantId) => IsUnrestricted || MerchantIds.Contains(merchantId);
}

public sealed record ReportingPeriod(DateTime From, DateTime To);

public sealed record CurrencyTotal(string Currency, decimal Amount, long Count);

public sealed record ReportingBreakdown(
    string Key,
    string Label,
    string Currency,
    decimal Amount,
    long Count);

public sealed record AdminDashboardResult(
    ReportingPeriod Period,
    IReadOnlyList<CurrencyTotal> Totals,
    long TransactionCount,
    long SuccessCount,
    long FailureCount,
    long PendingCount,
    IReadOnlyList<ReportingBreakdown> ByPsp,
    IReadOnlyList<ReportingBreakdown> ByMethod,
    IReadOnlyList<ReportingBreakdown> ByOriginator);

public sealed record AdminTransactionQuery(ReportingAccess Access) : PagedQuery;

public sealed record AdminTransactionListItem(
    Guid TransactionId,
    Guid OrderId,
    string OrderNo,
    Guid MerchantId,
    Guid? OriginatorId,
    decimal Amount,
    string Currency,
    string Method,
    string Psp,
    string Status,
    string SessionStatus,
    string OrderStatus,
    int ItemCount,
    string CustomerName,
    string CustomerPhone,
    string? CustomerEmail,
    string? ExternalChargeId,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? DataQualityCode,
    long Version);

public sealed record AdminTransactionOrderLine(
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    decimal UnitAmount,
    decimal DiscountAmount,
    string Currency);

public sealed record AdminTransactionLifecycleEvent(Guid EventId, string Status, DateTime At);

public sealed record AdminTransactionDetail(
    AdminTransactionListItem Transaction,
    IReadOnlyList<AdminTransactionOrderLine> Lines,
    IReadOnlyList<AdminTransactionLifecycleEvent> Lifecycle);

public sealed record TransactionCapability(
    string Code,
    bool Available,
    bool RequiresApproval,
    string? ReasonCode);

public static class TransactionProjectionRules
{
    public static (string Status, string? DataQualityCode) Normalize(
        string sessionStatus,
        string orderStatus)
    {
        var status = sessionStatus.ToLowerInvariant() switch
        {
            "created" or "redirected" => "pending",
            "paid" => "paid",
            "failed" => "failed",
            "expired" => "expired",
            _ => "unknown",
        };

        var conflict = (status, orderStatus.ToLowerInvariant()) switch
        {
            ("paid", "paid") => null,
            ("failed", "failed") => null,
            ("expired", "expired") => null,
            ("pending", "pending") => null,
            (_, "cancelled") when status == "pending" => "order_session_state_mismatch",
            ("unknown", _) => "unknown_payment_state",
            _ => "order_session_state_mismatch",
        };
        return (status, conflict);
    }

    public static IReadOnlyList<TransactionCapability> Capabilities(string orderStatus) =>
    [
        Capability("cancel", orderStatus, "pending"),
        Capability("resend_link", orderStatus, "pending"),
        new("extend_link", false, false, "capability_unavailable"),
        new("cancel_link", false, false, "capability_unavailable"),
        new("capture", false, false, "capability_unavailable"),
        new("void", false, false, "capability_unavailable"),
        new("refund", false, true, "capability_unavailable"),
        new("receipt", false, false, "capability_unavailable"),
    ];

    private static TransactionCapability Capability(string code, string actual, string required) =>
        string.Equals(actual, required, StringComparison.OrdinalIgnoreCase)
            ? new(code, true, false, null)
            : new(code, false, false, "state_conflict");
}

public interface IAdminReportingReader
{
    Task<AdminDashboardResult> DashboardAsync(
        ReportingPeriod period,
        ReportingAccess access,
        Guid? merchantId,
        CancellationToken cancellationToken);

    Task<PagedResult<AdminTransactionListItem>> ListTransactionsAsync(
        AdminTransactionQuery query,
        CancellationToken cancellationToken);

    Task<AdminTransactionDetail?> GetTransactionAsync(
        Guid paymentSessionId,
        ReportingAccess access,
        CancellationToken cancellationToken);
}
