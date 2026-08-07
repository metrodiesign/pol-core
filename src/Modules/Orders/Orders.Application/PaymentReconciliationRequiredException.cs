namespace Orders.Application;

/// <summary>Poison-path signal for collected money that conflicts with terminal Order state.</summary>
public sealed class PaymentReconciliationRequiredException : Exception
{
    public PaymentReconciliationRequiredException(Guid eventId, Guid orderId, Guid paymentSessionId)
        : base($"Payment reconciliation required: EventId={eventId}; OrderId={orderId}; PaymentSessionId={paymentSessionId}.")
    {
    }
}
