using BuildingBlocks.Application;
using Contracts;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

public sealed class ResendOrderSummaryHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTime Created = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Resend_rotates_the_token_extends_the_expiry_and_saves()
    {
        var order = Order.Create(TenantId, Money.Of(15000, "THB"), Created);
        var oldToken = order.SummaryToken;
        var clock = new FixedClock { UtcNow = Created.AddHours(5) };
        var uow = new FakeUnitOfWork();
        var outbox = new FakeOutbox();
        var handler = new ResendOrderSummaryHandler(new FakeOrderRepository(order), outbox, uow, clock);

        var result = await handler.Handle(new ResendOrderSummaryCommand(order.Id, TenantId), default);

        Assert.NotEqual(oldToken, result.SummaryToken);
        Assert.Equal(clock.UtcNow + Order.SummaryTokenTtl, result.ExpiresAtUtc);
        Assert.Equal(1, uow.SaveCount);
        Assert.Empty(outbox.Enqueued); // no recipient captured -> nothing to notify
    }

    [Fact]
    public async Task Resend_enqueues_a_notification_with_the_rotated_token_when_a_recipient_was_captured()
    {
        var order = Order.Create(TenantId, Money.Of(15000, "THB"), Created, notificationRecipient: "buyer@example.com");
        var clock = new FixedClock { UtcNow = Created.AddHours(5) };
        var outbox = new FakeOutbox();
        var handler = new ResendOrderSummaryHandler(new FakeOrderRepository(order), outbox, new FakeUnitOfWork(), clock);

        var result = await handler.Handle(new ResendOrderSummaryCommand(order.Id, TenantId), default);

        var note = Assert.IsType<CustomerOrderNotification>(Assert.Single(outbox.Enqueued));
        Assert.Equal("buyer@example.com", note.Recipient);
        Assert.Equal(result.SummaryToken, note.SummaryToken); // carries the NEW link, not the invalidated one
        Assert.Equal(order.Id, note.OrderId);
        Assert.Equal(TenantId, note.TenantId);
    }

    [Fact]
    public async Task Resend_rejects_an_unknown_order()
    {
        var handler = new ResendOrderSummaryHandler(new FakeOrderRepository(), new FakeOutbox(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new ResendOrderSummaryCommand(Guid.NewGuid(), TenantId), default));
    }

    [Fact]
    public async Task Resend_rejects_an_order_that_is_no_longer_awaiting_payment()
    {
        var order = Order.Create(TenantId, Money.Of(15000, "THB"), Created);
        order.Cancel();
        var handler = new ResendOrderSummaryHandler(new FakeOrderRepository(order), new FakeOutbox(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new ResendOrderSummaryCommand(order.Id, TenantId), default));
    }
}
