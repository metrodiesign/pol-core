using BuildingBlocks.Application;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

/// <summary>Order cancel (purchase-flow-completion REQ-4.1/4.4/4.5). The state machine itself already lives
/// in <see cref="Order.Cancel"/>; what these pin is that the handler does not soften it — a paid order still
/// refuses, a cancelled one still succeeds, and neither answer is invented at this layer.</summary>
public sealed class CancelOrderHandlerTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly Money Amount = Money.Of(15000, "THB");
    private static readonly DateTime Created = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);

    private static Order NewOrder() => Order.Create(MerchantId, Amount, Created, OrderLineInputs.OneLine(Amount), orderNo: "ORD6900000001");

    private static (CancelOrderHandler Handler, FakeUnitOfWork UnitOfWork, FakePaymentSessionProbe Probe) NewHandler(
        bool sessionBlocks = false, params Order[] orders)
    {
        var uow = new FakeUnitOfWork();
        var probe = new FakePaymentSessionProbe(uow, sessionBlocks);
        return (new CancelOrderHandler(new FakeOrderRepository(orders), probe, uow), uow, probe);
    }

    [Fact]
    public async Task Cancelling_an_unpaid_order_cancels_it_and_saves()
    {
        var order = NewOrder();
        var (handler, uow, _) = NewHandler(orders: order);

        var result = await handler.Handle(new CancelOrderCommand(order.Id), default);

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(nameof(OrderStatus.Cancelled), result.Status);
        Assert.Equal(order.Id, result.OrderId);
        Assert.Equal(1, uow.SaveCount);
    }

    // REQ-9.18 — any non-Pending Order, including Cancelled, is a conflict.
    [Fact]
    public async Task Cancelling_an_already_cancelled_order_is_rejected()
    {
        var order = NewOrder();
        order.Cancel();
        var (handler, _, _) = NewHandler(orders: order);

        await Assert.ThrowsAsync<ConflictException>(
            () => handler.Handle(new CancelOrderCommand(order.Id), default).AsTask());
    }

    // REQ-4.4 — money has been collected for this order; cancelling it is never on the table.
    [Fact]
    public async Task Cancelling_a_paid_order_is_rejected()
    {
        var order = NewOrder();
        order.MarkPaid(Guid.NewGuid(), "card", Amount, Created.AddMinutes(1));
        var (handler, uow, _) = NewHandler(orders: order);

        await Assert.ThrowsAsync<ConflictException>(
            () => handler.Handle(new CancelOrderCommand(order.Id), default).AsTask());

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact]
    public async Task Cancelling_an_unknown_order_is_not_found()
    {
        var (handler, _, _) = NewHandler();

        await Assert.ThrowsAsync<NotFoundException>(
            () => handler.Handle(new CancelOrderCommand(Guid.NewGuid()), default).AsTask());
    }

    // REQ-4.7 — the race the release cannot see: a session minted between the endpoint's release and this
    // handler's commit. The post-flip probe finds it and the whole cancel aborts as a 409 conflict.
    [Fact]
    public async Task A_session_that_appeared_after_the_release_aborts_the_cancel()
    {
        var order = NewOrder();
        var (handler, _, _) = NewHandler(sessionBlocks: true, orders: order);

        await Assert.ThrowsAsync<ConflictException>(
            () => handler.Handle(new CancelOrderCommand(order.Id), default).AsTask());
    }

    // Order lock is acquired by GetForUpdateAsync before this probe; mutation/save happens after probe.
    [Fact]
    public async Task The_probe_runs_before_the_cancel_mutation_is_saved()
    {
        var order = NewOrder();
        var (handler, _, probe) = NewHandler(orders: order);

        await handler.Handle(new CancelOrderCommand(order.Id), default);

        Assert.Equal(0, probe.SaveCountWhenProbed);
    }
}
