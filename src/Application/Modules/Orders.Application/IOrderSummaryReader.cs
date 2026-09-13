using SharedKernel;

namespace Orders.Application;

/// <summary>Generic customer-summary line. Metadata is deliberately absent.</summary>
public sealed record OrderSummaryLine(
    string ProductCode, string VariantCode, string? VariantName, int Quantity, Money UnitPrice);

/// <summary>Order summary resolved from the public, token-keyed link. <see cref="MerchantId"/> is here for
/// ONE reason — the host binds the actor scope with it before any merchant-scoped work, exactly as the
/// webhook does — and MUST NOT be projected onto the customer's response: the customer is anonymous and
/// must not learn the merchant (REQ-8.4). <see cref="PaymentChannel"/> is the channel the merchant chose at
/// checkout, null only on orders written before that column existed. <see cref="ExpiresAt"/> lets the host
/// decide 410 Gone vs 200 on the summary read, and 404 on the customer payment endpoints (REQ-8.2).
/// There is deliberately no payment-session id: the payment state is read through <c>payment-status</c>
/// (REQ-8.9).</summary>
public sealed record OrderSummary(
    Guid OrderId, Guid MerchantId, string OrderNo, Money Amount, string Status, string? PaymentChannel,
    DateTime ExpiresAt, IReadOnlyList<OrderSummaryLine> Lines);

/// <summary>Reads an order summary by its opaque link token, bypassing RLS (the customer has no merchant
/// binding) via a stored proc that runs as the webhook-resolver principal. Returns null for an unknown
/// token; the host turns an expired one into 410.</summary>
public interface IOrderSummaryReader
{
    Task<OrderSummary?> GetByTokenAsync(string token, CancellationToken cancellationToken);
}
