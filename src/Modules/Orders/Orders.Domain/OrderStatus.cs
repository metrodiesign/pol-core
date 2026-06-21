namespace Orders.Domain;

/// <summary>Lifecycle of an <see cref="Order"/>. Created <see cref="AwaitingPayment"/>; moves to
/// <see cref="Paid"/> only on a PSP-confirmed payment (via the PaymentPaid integration event), or
/// to <see cref="Cancelled"/> when abandoned. Transitions are one-way out of a terminal state.</summary>
public enum OrderStatus
{
    AwaitingPayment = 0,
    Paid = 1,
    Cancelled = 2,
}
