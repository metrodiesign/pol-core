namespace Orders.Domain;

/// <summary>Lifecycle of an <see cref="Order"/>. Created <see cref="Pending"/>; moves to
/// <see cref="Paid"/> only on a PSP-confirmed payment (via the PaymentPaid integration event), or
/// to <see cref="Cancelled"/> when abandoned. Transitions are one-way out of a terminal state.</summary>
public enum OrderStatus
{
    Pending = 0,
    Paid = 1,
    Failed = 2,
    Expired = 3,
    Refunded = 4,
    Cancelled = 5,
}
