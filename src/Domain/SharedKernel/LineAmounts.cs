namespace SharedKernel;

/// <summary>
/// The per-line money arithmetic of a purchase (purchase-flow-completion REQ-6.3/6.4/6.5): gross premium,
/// the discount subtracted from it, and the net the customer is charged. One place, because the same rule is
/// applied by <c>Orders.Domain.Order.Create</c> and direct Cart-to-Order
/// endpoint's client-total check — three copies of a money rule is how the three drift apart.
/// </summary>
public static class LineAmounts
{
    /// <summary>Premium before discount: unit price x quantity.</summary>
    public static Money Gross(Money unitPrice, int quantity) =>
        Money.Of(unitPrice.Amount * quantity, unitPrice.Currency);

    /// <summary>
    /// Validates a line discount against its gross and normalises "no discount" to zero in the gross's own
    /// currency. A negative discount is already impossible (<see cref="Money.Of"/> rejects it); this adds
    /// REQ-6.4's other half — a discount may not exceed the line, nor be quoted in another currency.
    /// </summary>
    public static Money NormaliseDiscount(Money? discount, Money gross)
    {
        if (discount is not { } value)
            return Money.Zero(gross.Currency);

        if (!value.SameCurrencyAs(gross))
            throw new ArgumentException("Line discount currency must match the line currency.", nameof(discount));
        if (value.Amount > gross.Amount)
            throw new ArgumentException("Line discount must not exceed the line total.", nameof(discount));

        return value;
    }

    /// <summary>What the line actually costs: gross minus discount. Callers pass a discount that
    /// <see cref="NormaliseDiscount"/> already vetted, so this cannot go negative.</summary>
    public static Money Net(Money gross, Money discount) =>
        Money.Of(gross.Amount - discount.Amount, gross.Currency);
}
