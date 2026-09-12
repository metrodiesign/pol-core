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
    private static readonly Guid MerchantId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTime At = new(2026, 6, 21, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid PaymentSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Order NewOrder(decimal amount = 15000m, string currency = "THB") =>
        Order.Create(MerchantId, Money.Of(amount, currency), At,
            OrderLineInputs.OneLine(Money.Of(amount, currency)), orderNo: "ORD6900000001",
            paymentChannel: "card");

    [Fact]
    public void OrderPaymentMethodInvariant_rejects_settlement_method_mismatch_without_mutating_Order()
    {
        var order = NewOrder();

        Assert.Throws<InvalidOperationException>(() =>
            order.MarkPaid(PaymentSessionId, "promptpay", Money.Of(15000, "THB"), At));

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal("card", order.PaymentChannel);
        Assert.Null(order.PaymentSessionId);
    }

    [Fact]
    public void MarkPaid_with_matching_amount_transitions_to_Paid_and_raises_event_once()
    {
        var order = NewOrder();

        var transitioned = order.MarkPaid(PaymentSessionId, "card", Money.Of(15000, "THB"), At.AddMinutes(1));

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

        Assert.Throws<InvalidOperationException>(() =>
            order.MarkPaid(PaymentSessionId, "card", Money.Of(14999, "THB"), At));

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void MarkPaid_rejects_a_different_currency()
    {
        var order = NewOrder(15000, "THB");

        // Same numeric minor units, different currency — must not fulfil (PLAN #2).
        Assert.Throws<InvalidOperationException>(() =>
            order.MarkPaid(PaymentSessionId, "card", Money.Of(15000, "USD"), At));

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void MarkPaid_is_idempotent_when_already_Paid()
    {
        var order = NewOrder();
        Assert.True(order.MarkPaid(PaymentSessionId, "card", Money.Of(15000, "THB"), At));
        order.ClearDomainEvents();

        // A replayed PaymentPaid: no-op, returns false, raises no further event.
        var transitionedAgain = order.MarkPaid(
            PaymentSessionId, "card", Money.Of(15000, "THB"), At.AddMinutes(5));

        Assert.False(transitionedAgain);
        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void Replayed_PaymentPaid_still_rejects_mismatched_money()
    {
        var order = NewOrder();
        Assert.True(order.MarkPaid(PaymentSessionId, "card", Money.Of(15000, "THB"), At));

        Assert.Throws<InvalidOperationException>(() =>
            order.MarkPaid(PaymentSessionId, "card", Money.Of(14999, "THB"), At.AddMinutes(1)));

        Assert.Equal(OrderStatus.Paid, order.Status);
    }

    [Fact]
    public void MarkPaid_on_a_cancelled_order_throws()
    {
        var order = NewOrder();
        order.Cancel();

        Assert.Throws<InvalidOperationException>(() =>
            order.MarkPaid(PaymentSessionId, "card", Money.Of(15000, "THB"), At));
        Assert.Equal(OrderStatus.Cancelled, order.Status);
    }
}
