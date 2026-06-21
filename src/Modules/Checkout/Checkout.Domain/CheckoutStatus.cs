namespace Checkout.Domain;

/// <summary>Lifecycle of a <see cref="CheckoutSession"/>: opened (<see cref="Started"/>), turned
/// into an order/payment intent (<see cref="Confirmed"/>), or dropped (<see cref="Abandoned"/>).</summary>
public enum CheckoutStatus
{
    Started = 0,
    Confirmed = 1,
    Abandoned = 2,
}
