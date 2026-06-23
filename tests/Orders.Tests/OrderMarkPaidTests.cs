using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

/// <summary>
/// Pure domain tests for <see cref="Order.MarkPaid"/> — no DB. They pin PLAN decision #2 (the order
/// re-verifies the paid amount AND currency, never trusting the event's id alone) and PLAN decision
/// #10 (idempotent: a replayed PaymentPaid never double-fulfils, and only the first transition raises
/// the OrderPaid domain event).
/// </summary>
public sealed class OrderMarkPaidTests
{
    private static readonly Guid TenantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime At = new(2026, 6, 21, 9, 0, 0, DateTimeKind.Utc);

    private static Order NewOrder(long minorUnits = 15000, string currency = "THB") =>
        Order.Create(TenantId, Money.Of(minorUnits, currency), At);

    [Fact]
    public void MarkPaid_with_matching_amount_transitions_to_Paid_and_raises_event_once()
    {
        var order = NewOrder();

        var transitioned = order.MarkPaid(Money.Of(15000, "THB"), At.AddMinutes(1));

        Assert.True(transitioned);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(At.AddMinutes(1), order.PaidAt);
        Assert.Single(order.DomainEvents);
        Assert.IsType<OrderPaid>(Assert.Single(order.DomainEvents));
    }

    [Fact]
    public void MarkPaid_rejects_a_different_amount()
    {
        var order = NewOrder(15000, "THB");

        Assert.Throws<InvalidOperationException>(() => order.MarkPaid(Money.Of(14999, "THB"), At));

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void MarkPaid_rejects_a_different_currency()
    {
        var order = NewOrder(15000, "THB");

        // Same numeric minor units, different currency — must not fulfil (PLAN #2).
        Assert.Throws<InvalidOperationException>(() => order.MarkPaid(Money.Of(15000, "USD"), At));

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void MarkPaid_is_idempotent_when_already_Paid()
    {
        var order = NewOrder();
        Assert.True(order.MarkPaid(Money.Of(15000, "THB"), At));
        order.ClearDomainEvents();

        // A replayed PaymentPaid: no-op, returns false, raises no further event.
        var transitionedAgain = order.MarkPaid(Money.Of(15000, "THB"), At.AddMinutes(5));

        Assert.False(transitionedAgain);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void MarkPaid_on_a_cancelled_order_throws()
    {
        var order = NewOrder();
        order.Cancel();

        Assert.Throws<InvalidOperationException>(() => order.MarkPaid(Money.Of(15000, "THB"), At));
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }
}
