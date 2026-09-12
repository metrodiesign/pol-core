using Contracts;
using Orders.Application;

namespace Orders.Tests;

/// <summary>Customer notification (REQ-3): order creation enqueues the notification in the same unit of
/// work when a recipient is present (and only then); the background consumer sends it via the port and
/// propagates a delivery failure so the outbox retries.</summary>
public sealed class CustomerNotificationTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();

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
