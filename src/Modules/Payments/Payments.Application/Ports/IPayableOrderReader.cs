using SharedKernel;

namespace Payments.Application.Ports;

/// <summary>The order's lifecycle as the payment path reads it — a seam-local twin of Orders' own
/// <c>OrderStatus</c>, because Payments never references Orders. The reader maps that enum onto this one
/// EXPLICITLY, so a status added there stops at the seam instead of silently reading as payable.</summary>
public enum PayableOrderStatus
{
    AwaitingPayment = 0,
    Paid = 1,
    Cancelled = 2,
}

/// <summary>
/// The only order facts a payment session needs: the amount to charge and where the order stands.
/// Deliberately carries no line/PII data — the merchant-facing order detail read (which writes a reveal
/// audit) must never be on the payment path.
/// </summary>
public sealed record PayableOrder(Guid OrderId, Money Amount, PayableOrderStatus Status)
{
    /// <summary>The only distinction create-session needs; the customer's status check needs the full
    /// three, which is why the status itself is what crosses the port.</summary>
    public bool IsAwaitingPayment => Status is PayableOrderStatus.AwaitingPayment;
}

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
