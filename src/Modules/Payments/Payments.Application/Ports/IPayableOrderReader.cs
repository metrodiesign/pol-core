using SharedKernel;

namespace Payments.Application.Ports;

/// <summary>
/// The only order facts a payment session needs: the amount to charge and whether the order is still
/// awaiting payment. Deliberately carries no line/PII data — the merchant-facing order detail read (which
/// writes a reveal audit) must never be on the payment path.
/// </summary>
public sealed record PayableOrder(Guid OrderId, Money Amount, bool IsAwaitingPayment);

/// <summary>
/// Read port for the order a payment session prices itself from. Declared here and implemented in
/// <c>Persistence.MerchantRuntime</c> so Payments never references <c>Orders.Application</c> — the same
/// seam shape as <c>IWebhookMerchantResolver</c>.
/// </summary>
public interface IPayableOrderReader
{
    /// <summary>Reads the order under the bound merchant's query filter; null when it does not exist for
    /// that merchant (an order under another merchant is indistinguishable from a missing one).</summary>
    Task<PayableOrder?> GetAsync(Guid orderId, CancellationToken cancellationToken);
}
