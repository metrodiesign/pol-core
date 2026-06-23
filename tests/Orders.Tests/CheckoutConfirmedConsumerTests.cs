using Contracts;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

/// <summary>CheckoutConfirmed -> Order (REQ-5.2/5.3/5.4): the consumer opens an order (with the checkout
/// session id as the idempotency key) and enqueues the notification when a recipient was carried; a replay
/// whose session already has an order does nothing.</summary>
public sealed class CheckoutConfirmedConsumerTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task It_creates_an_order_and_enqueues_the_notification_for_a_new_checkout()
    {
        var orders = new FakeOrderRepository();
        var outbox = new FakeOutbox();
        var consumer = new CheckoutConfirmedConsumer(orders, outbox, new FakeUnitOfWork(), new FixedClock());
        var sessionId = Guid.NewGuid();

        await consumer.Handle(new CheckoutConfirmed(Tenant, sessionId, 15000, "THB", "buyer@example.com", At), default);

        var order = Assert.Single(orders.All);
        Assert.Equal(sessionId, order.CheckoutSessionId);
        Assert.Equal(15000, order.AmountMinorUnits);
        var note = Assert.IsType<CustomerOrderNotification>(Assert.Single(outbox.Enqueued));
        Assert.Equal(order.Id, note.OrderId);
    }

    [Fact]
    public async Task It_skips_when_an_order_already_exists_for_the_session()
    {
        var sessionId = Guid.NewGuid();
        var existing = Order.Create(Tenant, Money.Of(15000, "THB"), At, checkoutSessionId: sessionId);
        var orders = new FakeOrderRepository(existing);
        var outbox = new FakeOutbox();
        var consumer = new CheckoutConfirmedConsumer(orders, outbox, new FakeUnitOfWork(), new FixedClock());

        await consumer.Handle(new CheckoutConfirmed(Tenant, sessionId, 15000, "THB", null, At), default);

        Assert.Single(orders.All);      // still just the original — no second order
        Assert.Empty(outbox.Enqueued);  // and no notification
    }

    [Fact]
    public async Task It_creates_an_order_without_a_notification_when_no_recipient()
    {
        var orders = new FakeOrderRepository();
        var outbox = new FakeOutbox();
        var consumer = new CheckoutConfirmedConsumer(orders, outbox, new FakeUnitOfWork(), new FixedClock());

        await consumer.Handle(new CheckoutConfirmed(Tenant, Guid.NewGuid(), 15000, "THB", null, At), default);

        Assert.Single(orders.All);
        Assert.Empty(outbox.Enqueued);
    }
}
