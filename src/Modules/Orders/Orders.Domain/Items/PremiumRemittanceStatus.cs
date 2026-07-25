namespace Orders.Domain.Items;

/// <summary>Whether the premium has been remitted (deducted) to the external insurer (REQ-2.1) —
/// independent of the customer's own payment status (<see cref="Orders.Domain.OrderStatus"/>). Defaults to
/// <see cref="NotApplicable"/> until an operator records a <see cref="Deducted"/> date.</summary>
public enum PremiumRemittanceStatus
{
    NotApplicable = 0,
    Deducted = 1,
}
