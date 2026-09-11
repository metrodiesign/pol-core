namespace Orders.Application;

/// <summary>
/// Cancel's post-flip re-check against the Payments cluster (REQ-4.7). Declared here and implemented in
/// <c>Persistence.MerchantRuntime</c> so Orders never references Payments — the mirror of
/// <c>IPayableOrderReader</c>'s seam in the other direction.
/// </summary>
public interface IPaymentSessionProbe
{
    /// <summary>
    /// True when the order has a payment session a cancel must not commit over: one still chargeable
    /// (Created/Redirected — minted between the release check and the cancel's own flip) or one already
    /// Paid (money landed; the order's own flip to Paid may still be in flight on the outbox). Terminal
    /// sessions without payment (Expired/Failed) do not block.
    /// </summary>
    Task<bool> HasBlockingSessionAsync(Guid orderId, CancellationToken cancellationToken);
}
