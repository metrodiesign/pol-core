using Contracts;
using Orders.Application;
using SharedKernel;

namespace Orders.Tests;

/// <summary>Customer notification (REQ-3): order creation enqueues the notification in the same unit of
/// work when a recipient is present (and only then); the background consumer sends it via the port and
/// propagates a delivery failure so the outbox retries.</summary>
public sealed class CustomerNotificationTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();

    [Fact]
    public async Task CreateOrder_with_a_recipient_enqueues_the_notification()
    {
        var outbox = new FakeOutbox();
        var handler = new CreateOrderHandler(new FakeOrderRepository(), outbox, new FakeUnitOfWork(), new FixedClock());

        var result = await handler.Handle(
            new CreateOrderCommand(MerchantId, Money.Of(15000m, "THB"), Recipient: "buyer@example.com"), default);

        var note = Assert.IsType<CustomerOrderNotification>(Assert.Single(outbox.Enqueued));
        Assert.Equal(result.OrderId, note.OrderId);
        Assert.Equal("buyer@example.com", note.Recipient);
        Assert.False(string.IsNullOrWhiteSpace(note.SummaryToken));
    }

    [Fact]
    public async Task CreateOrder_without_a_recipient_enqueues_nothing()
    {
        var outbox = new FakeOutbox();
        var handler = new CreateOrderHandler(new FakeOrderRepository(), outbox, new FakeUnitOfWork(), new FixedClock());

        await handler.Handle(new CreateOrderCommand(MerchantId, Money.Of(15000m, "THB")), default);

        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task Consumer_sends_through_the_port()
    {
        var sender = new FakeNotificationSender();
        var consumer = new CustomerOrderNotificationConsumer(sender);

        await consumer.Handle(
            new CustomerOrderNotification(MerchantId, Guid.NewGuid(), "buyer@example.com", "tok", default), default);

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Consumer_propagates_a_delivery_failure_for_outbox_retry()
    {
        var consumer = new CustomerOrderNotificationConsumer(new FakeNotificationSender { ShouldThrow = true });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await consumer.Handle(
                new CustomerOrderNotification(MerchantId, Guid.NewGuid(), "buyer@example.com", "tok", default), default));
    }
}
