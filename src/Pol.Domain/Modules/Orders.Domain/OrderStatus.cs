namespace Orders.Domain;

/// <summary>Lifecycle of an <see cref="Order"/>. Created <see cref="Pending"/>; moves to
/// <see cref="Paid"/> only on a PSP-confirmed payment (via the PaymentPaid integration event), or
/// to <see cref="Cancelled"/> when abandoned. Transitions are one-way out of a terminal state.</summary>
public enum OrderStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Expired = 4,
    Refunded = 5,
    Cancelled = 6,
    /// <summary>Task 5 draft before the first issue command.</summary>
    Draft = 7,
    /// <summary>Task 5 issued order; payment state is read from <see cref="PaymentStatus"/>.</summary>
    Open = 8,
}
