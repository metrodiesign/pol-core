using SharedKernel;

namespace Orders.Application;

/// <summary>One line on the customer summary — insured person's name as-is, but
/// <see cref="MaskedInsuredIdNumber"/> is ALREADY masked by the reader (no reveal audit on this surface,
/// so nothing full-value ever leaves it), and <see cref="OrderSummaryLine"/> deliberately has NO
/// date-of-birth field at all — this surface never serializes it (design.md Decision #3, locked).</summary>
public sealed record OrderSummaryLine(Guid ProductId, string InsuredFirstName, string InsuredLastName, string MaskedInsuredIdNumber);

/// <summary>Customer-safe order summary returned by the public, token-keyed link. Carries NO merchant id —
/// the customer is anonymous and must not learn the merchant. <see cref="ExpiresAt"/> lets the host decide
/// 410 Gone vs 200.</summary>
public sealed record OrderSummary(
    Guid OrderId, string OrderNo, Money Amount, string Status, Guid? PaymentSessionId, DateTime ExpiresAt,
    IReadOnlyList<OrderSummaryLine> Lines);

/// <summary>Reads an order summary by its opaque link token, bypassing RLS (the customer has no merchant
/// binding) via a stored proc that runs as the webhook-resolver principal. Returns null for an unknown
/// token; the host turns an expired one into 410.</summary>
public interface IOrderSummaryReader
{
    Task<OrderSummary?> GetByTokenAsync(string token, CancellationToken cancellationToken);
}
