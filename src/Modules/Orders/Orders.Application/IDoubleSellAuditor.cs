namespace Orders.Application;

/// <summary>
/// Reports the one thing this platform must never do quietly: the same insurance document paid for by two
/// different orders (products-external-source-of-truth REQ-5.16/8.2). Declared here and implemented in
/// <c>Persistence.MerchantRuntime</c> because <c>Orders.Application</c> cannot log — its csproj is owned by
/// the spine and references no logging package (the same constraint <c>OrderPaidConsumer</c> already records
/// in a comment) — and because only that assembly can read <c>shop.OrderItems</c> across merchants.
/// </summary>
public interface IDoubleSellAuditor
{
    /// <summary>
    /// Called at the moment an order really transitions to <c>Paid</c> — never on a replay, so a redelivered
    /// <c>PaymentPaid</c> does not page anyone. The order being processed is NOT counted as the second holder
    /// (REQ-5.16): only a DIFFERENT Paid order holding one of its documents is a double sale. Never throws:
    /// this is a report about a payment that already happened, not a gate on it.
    /// </summary>
    Task ReportIfDoubleSoldAsync(Guid orderId, CancellationToken cancellationToken);
}
