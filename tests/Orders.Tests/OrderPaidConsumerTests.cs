using Contracts;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

/// <summary>
/// PaymentPaid -> Order fulfilment (bugfix-order-paid-link F1-F4, B1-B2): the consumer resolves the
/// order by the event's <c>OrderId</c> — production orders are created without a payment session id
/// (see CheckoutConfirmedConsumer), so a PaymentSessionId join can never match — fulfils it once,
/// and lets amount-mismatch / cancelled-order violations escape so the dispatcher parks the message
/// in the DLQ instead of acking it silently.
/// </summary>
public sealed class OrderPaidConsumerTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    // Mirrors the production path: CheckoutConfirmedConsumer opens orders WITHOUT a payment session id.
    private static Order ProductionOrder(decimal amount = 15000m, string currency = "THB") =>
        Order.Create(
            Merchant, Money.Of(amount, currency), At, OrderLineInputs.OneLine(Money.Of(amount, currency)),
            checkoutSessionId: Guid.NewGuid());

    private static PaymentPaid PaidEvent(Order order, decimal amount = 15000m, string currency = "THB") => new(
        PaymentSessionId: Guid.NewGuid(),
        OrderId: order.Id,
        MerchantId: Merchant,
        Amount: Money.Of(amount, currency),
        PspCode: "2c2p",
        ExternalChargeId: "chg_abc123",
        EventId: "evt_xyz789",
        OccurredAt: At.AddMinutes(5));

    [Fact] // F1 + F2 — the repro: the exact production shape that, before the fix, never fulfilled.
    public async Task It_marks_the_order_paid_by_the_events_OrderId()
    {
        var order = ProductionOrder();
        var orders = new FakeOrderRepository(order);
        var uow = new FakeUnitOfWork();

        await new OrderPaidConsumer(orders, uow).Handle(PaidEvent(order), default);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(At.AddMinutes(5), order.PaidAt);
        Assert.IsType<OrderPaid>(Assert.Single(order.DomainEvents));
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact] // F3 — amount mismatch must escape (dispatcher MarkFailed -> retry -> DLQ), never ack silently.
    public async Task It_lets_an_amount_mismatch_escape_without_transitioning()
    {
        var order = ProductionOrder(15000, "THB");
        var uow = new FakeUnitOfWork();
        var consumer = new OrderPaidConsumer(new FakeOrderRepository(order), uow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await consumer.Handle(PaidEvent(order, 14999, "THB"), default));

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Empty(order.DomainEvents);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // F4 — money arrived on a cancelled order: park loud in the DLQ, never mark Paid.
    public async Task It_lets_a_cancelled_order_payment_escape_without_transitioning()
    {
        var order = ProductionOrder();
        order.Cancel();
        var uow = new FakeUnitOfWork();
        var consumer = new OrderPaidConsumer(new FakeOrderRepository(order), uow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await consumer.Handle(PaidEvent(order), default));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.DoesNotContain(order.DomainEvents, e => e is OrderPaid);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // B1 — replayed event on an already-Paid order: idempotent no-op, no second event or save.
    public async Task It_noops_on_a_replayed_event_for_a_paid_order()
    {
        var order = ProductionOrder();
        Assert.True(order.MarkPaid(Money.Of(15000, "THB"), At.AddMinutes(5)));
        order.ClearDomainEvents();
        var uow = new FakeUnitOfWork();

        await new OrderPaidConsumer(new FakeOrderRepository(order), uow).Handle(PaidEvent(order), default);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(order.DomainEvents);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // B2 — at-least-once delivery: an event whose order this module has no row for is acked, not thrown.
    public async Task It_acks_an_event_whose_order_is_unknown()
    {
        var uow = new FakeUnitOfWork();
        var stray = ProductionOrder();

        await new OrderPaidConsumer(new FakeOrderRepository(), uow).Handle(PaidEvent(stray), default);

        Assert.Equal(0, uow.SaveCount);
    }
}
