using BuildingBlocks.Application;
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
        var handler = new ResendOrderSummaryHandler(new FakeOrderRepository(order), uow, clock);

        var result = await handler.Handle(new ResendOrderSummaryCommand(order.Id, TenantId), default);

        Assert.NotEqual(oldToken, result.SummaryToken);
        Assert.Equal(clock.UtcNow + Order.SummaryTokenTtl, result.ExpiresAtUtc);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task Resend_rejects_an_unknown_order()
    {
        var handler = new ResendOrderSummaryHandler(new FakeOrderRepository(), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<NotFoundException>(async () =>
            await handler.Handle(new ResendOrderSummaryCommand(Guid.NewGuid(), TenantId), default));
    }

    [Fact]
    public async Task Resend_rejects_an_order_that_is_no_longer_awaiting_payment()
    {
        var order = Order.Create(TenantId, Money.Of(15000, "THB"), Created);
        order.Cancel();
        var handler = new ResendOrderSummaryHandler(new FakeOrderRepository(order), new FakeUnitOfWork(), new FixedClock());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.Handle(new ResendOrderSummaryCommand(order.Id, TenantId), default));
    }
}
