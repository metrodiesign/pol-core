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
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<CheckoutConfirmedLine> OneLine() =>
        [new CheckoutConfirmedLine(
            Product, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", Dob)];

    [Fact]
    public async Task It_creates_an_order_and_enqueues_the_notification_for_a_new_checkout()
    {
        var orders = new FakeOrderRepository();
        var outbox = new FakeOutbox();
        var consumer = new CheckoutConfirmedConsumer(orders, outbox, new FakeUnitOfWork(), new FixedClock());
        var sessionId = Guid.NewGuid();

        await consumer.Handle(
            new CheckoutConfirmed(Merchant, sessionId, Money.Of(15000m, "THB"), "buyer@example.com", At, OneLine()),
            default);

        var order = Assert.Single(orders.All);
        Assert.Equal(sessionId, order.CheckoutSessionId);
        Assert.Equal(Money.Of(15000m, "THB"), order.Amount);
        Assert.Single(order.Lines);
        var note = Assert.IsType<CustomerOrderNotification>(Assert.Single(outbox.Enqueued));
        Assert.Equal(order.Id, note.OrderId);
    }

    [Fact]
    public async Task It_skips_when_an_order_already_exists_for_the_session()
    {
        var sessionId = Guid.NewGuid();
        var existing = Order.Create(
            Merchant, Money.Of(15000m, "THB"), At, OrderLineInputs.OneLine(Money.Of(15000m, "THB")),
            checkoutSessionId: sessionId);
        var orders = new FakeOrderRepository(existing);
        var outbox = new FakeOutbox();
        var consumer = new CheckoutConfirmedConsumer(orders, outbox, new FakeUnitOfWork(), new FixedClock());

        await consumer.Handle(new CheckoutConfirmed(Merchant, sessionId, Money.Of(15000m, "THB"), null, At, OneLine()), default);

        Assert.Single(orders.All);      // still just the original — no second order
        Assert.Empty(outbox.Enqueued);  // and no notification
    }

    [Fact]
    public async Task It_creates_an_order_without_a_notification_when_no_recipient()
    {
        var orders = new FakeOrderRepository();
        var outbox = new FakeOutbox();
        var consumer = new CheckoutConfirmedConsumer(orders, outbox, new FakeUnitOfWork(), new FixedClock());

        await consumer.Handle(new CheckoutConfirmed(Merchant, Guid.NewGuid(), Money.Of(15000m, "THB"), null, At, OneLine()), default);

        Assert.Single(orders.All);
        Assert.Empty(outbox.Enqueued);
    }
}
