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
/// in the DLQ instead of acking it silently. On a real transition it also enqueues
/// <see cref="Contracts.OrderPaid"/> in the same unit of work (REQ-7.1), so Products can retire the
/// sold documents; a replay never re-enqueues.
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
        var outbox = new FakeOutbox();
        var uow = new FakeUnitOfWork();

        await new OrderPaidConsumer(orders, outbox, uow).Handle(PaidEvent(order), default);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(At.AddMinutes(5), order.PaidAt);
        Assert.IsType<Orders.Domain.OrderPaid>(Assert.Single(order.DomainEvents));
        Assert.Equal(1, uow.SaveCount);
    }

    // REQ-7.1 — a real transition enqueues Contracts.OrderPaid carrying the merchant + line product ids,
    // plus the order itself (REQ-5.4): this is the only emitter, so an unfilled OrderId would leave the
    // Products consumer permanently unable to tell a redelivery from a document sold twice.
    [Fact]
    public async Task It_enqueues_OrderPaid_with_the_lines_product_ids_on_transition()
    {
        var order = ProductionOrder();
        var expectedProductIds = order.Items.Select(i => i.ProductId).ToList();
        var outbox = new FakeOutbox();

        await new OrderPaidConsumer(new FakeOrderRepository(order), outbox, new FakeUnitOfWork()).Handle(PaidEvent(order), default);

        var paid = Assert.IsType<Contracts.OrderPaid>(Assert.Single(outbox.Enqueued));
        Assert.Equal(Merchant, paid.MerchantId);
        Assert.Equal(expectedProductIds, paid.ProductIds);
        Assert.Equal(At.AddMinutes(5), paid.OccurredAt);
        Assert.Equal(order.Id, paid.OrderId);
    }

    [Fact] // F3 — amount mismatch must escape (dispatcher MarkFailed -> retry -> DLQ), never ack silently.
    public async Task It_lets_an_amount_mismatch_escape_without_transitioning()
    {
        var order = ProductionOrder(15000, "THB");
        var outbox = new FakeOutbox();
        var uow = new FakeUnitOfWork();
        var consumer = new OrderPaidConsumer(new FakeOrderRepository(order), outbox, uow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await consumer.Handle(PaidEvent(order, 14999, "THB"), default));

        Assert.Equal(OrderStatus.AwaitingPayment, order.Status);
        Assert.Empty(order.DomainEvents);
        Assert.Empty(outbox.Enqueued);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // F4 — money arrived on a cancelled order: park loud in the DLQ, never mark Paid.
    public async Task It_lets_a_cancelled_order_payment_escape_without_transitioning()
    {
        var order = ProductionOrder();
        order.Cancel();
        var outbox = new FakeOutbox();
        var uow = new FakeUnitOfWork();
        var consumer = new OrderPaidConsumer(new FakeOrderRepository(order), outbox, uow);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await consumer.Handle(PaidEvent(order), default));

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.DoesNotContain(order.DomainEvents, e => e is Orders.Domain.OrderPaid);
        Assert.Empty(outbox.Enqueued);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // B1 — replayed event on an already-Paid order: idempotent no-op, no second event, no enqueue, no save.
    public async Task It_noops_on_a_replayed_event_for_a_paid_order()
    {
        var order = ProductionOrder();
        Assert.True(order.MarkPaid(Money.Of(15000, "THB"), At.AddMinutes(5)));
        order.ClearDomainEvents();
        var outbox = new FakeOutbox();
        var uow = new FakeUnitOfWork();

        await new OrderPaidConsumer(new FakeOrderRepository(order), outbox, uow).Handle(PaidEvent(order), default);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Empty(order.DomainEvents);
        Assert.Empty(outbox.Enqueued);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact] // B2 — at-least-once delivery: an event whose order this module has no row for is acked, not thrown.
    public async Task It_acks_an_event_whose_order_is_unknown()
    {
        var outbox = new FakeOutbox();
        var uow = new FakeUnitOfWork();
        var stray = ProductionOrder();

        await new OrderPaidConsumer(new FakeOrderRepository(), outbox, uow).Handle(PaidEvent(stray), default);

        Assert.Empty(outbox.Enqueued);
        Assert.Equal(0, uow.SaveCount);
    }
}
