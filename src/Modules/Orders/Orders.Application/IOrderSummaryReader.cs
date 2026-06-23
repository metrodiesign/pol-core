namespace Orders.Application;

/// <summary>Customer-safe order summary returned by the public, token-keyed link. Carries NO tenant id —
/// the customer is anonymous and must not learn the tenant. <see cref="ExpiresAt"/> lets the host decide
/// 410 Gone vs 200.</summary>
public sealed record OrderSummary(
    Guid OrderId, long AmountMinorUnits, string Currency, string Status, Guid? PaymentSessionId, DateTime ExpiresAt);

/// <summary>Reads an order summary by its opaque link token, bypassing RLS (the customer has no tenant
/// binding) via a stored proc that runs as the webhook-resolver principal. Returns null for an unknown
/// token; the host turns an expired one into 410.</summary>
public interface IOrderSummaryReader
{
    Task<OrderSummary?> GetByTokenAsync(string token, CancellationToken cancellationToken);
}
