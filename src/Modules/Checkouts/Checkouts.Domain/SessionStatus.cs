namespace Checkouts.Domain;

/// <summary>Lifecycle of a <see cref="Session"/>: opened (<see cref="Started"/>), turned
/// into an order/payment intent (<see cref="Confirmed"/>), or dropped (<see cref="Abandoned"/>).</summary>
public enum SessionStatus
{
    Started = 0,
    Confirmed = 1,
    Abandoned = 2,
}
