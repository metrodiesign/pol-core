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
/// in the DLQ instead of acking it silently. On a real transition it also runs the double-sell audit
/// (products-external-source-of-truth REQ-5.16/8.2) — the catalogue mirror and its <c>Contracts.OrderPaid</c>
/// integration event were retired (REQ-8.3), so nothing is enqueued any more; a replay never re-audits.
/// </summary>
public sealed class OrderPaidConsumerTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    // Mirrors the production path: CheckoutConfirmedConsumer opens orders WITHOUT a payment session id.
    private static Order ProductionOrder(decimal amount = 15000m, string currency = "THB") =>
        Order.Create(
            Merchant, Money.Of(amount, currency), At, OrderLineInputs.OneLine(Money.Of(amount, currency)),
            checkoutSessionId: Guid.NewGuid(), orderNo: "ORD6900000001");

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
        var auditor = new FakeDoubleSellAuditor();

        await new OrderPaidConsumer(orders, uow, auditor).Handle(PaidEvent(order), default);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(At.AddMinutes(5), order.PaidAt);
        Assert.IsType<Orders.Domain.OrderPaid>(Assert.Single(order.DomainEvents));
        Assert.Equal(1, uow.SaveCount);
    }

    // REQ-5.16/8.2 — a real transition runs the double-sell audit for the order that just became Paid, once,
    // AFTER the save. This is where a document sold twice is detected; it is deliberately not an enqueue any more.
    [Fact]
    public async Task It_runs_the_double_sell_audit_on_transition()
    {
        var order = ProductionOrder();
        var auditor = new FakeDoubleSellAuditor();

        await new OrderPaidConsumer(new FakeOrderRepository(order), new FakeUnitOfWork(), auditor).Handle(PaidEvent(order), default);

        Assert.Equal(order.Id, Assert.Single(auditor.Reported));
    }

    [Fact] // F3 — amount mismatch must escape (dispatcher MarkFailed -> retry -> DLQ), never ack silently.
    public async Task It_lets_an_amount_mismatch_escape_without_transitioning()
    {
        var order = ProductionOrder(15000, "THB");
        var uow = new FakeUnitOfWork();
        var auditor = new FakeDoubleSellAuditor();
        var consumer = new OrderPaidConsumer(new FakeOrderRepository(order), uow, auditor);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await consumer.Handle(PaidEvent(order, 14999, "THB"), default));

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Empty(order.DomainEvents);
        Assert.Empty(auditor.Reported);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // F4 — money arrived on a cancelled order: park loud in the DLQ, never mark Paid.
    public async Task It_lets_a_cancelled_order_payment_escape_without_transitioning()
    {
        var order = ProductionOrder();
        order.Cancel();
        var uow = new FakeUnitOfWork();
        var auditor = new FakeDoubleSellAuditor();
        var consumer = new OrderPaidConsumer(new FakeOrderRepository(order), uow, auditor);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await consumer.Handle(PaidEvent(order), default));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.DoesNotContain(order.DomainEvents, e => e is Orders.Domain.OrderPaid);
        Assert.Empty(auditor.Reported);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // B1 — replayed event on an already-Paid order: idempotent no-op, no second event, no audit, no save.
    public async Task It_noops_on_a_replayed_event_for_a_paid_order()
    {
        var order = ProductionOrder();
        Assert.True(order.MarkPaid(Money.Of(15000, "THB"), At.AddMinutes(5)));
        order.ClearDomainEvents();
        var uow = new FakeUnitOfWork();
        var auditor = new FakeDoubleSellAuditor();

        await new OrderPaidConsumer(new FakeOrderRepository(order), uow, auditor).Handle(PaidEvent(order), default);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(order.DomainEvents);
        Assert.Empty(auditor.Reported);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // B2 — at-least-once delivery: an event whose order this module has no row for is acked, not thrown.
    public async Task It_acks_an_event_whose_order_is_unknown()
    {
        var uow = new FakeUnitOfWork();
        var auditor = new FakeDoubleSellAuditor();
        var stray = ProductionOrder();

        await new OrderPaidConsumer(new FakeOrderRepository(), uow, auditor).Handle(PaidEvent(stray), default);

        Assert.Empty(auditor.Reported);
        Assert.Equal(0, uow.SaveCount);
    }
}
