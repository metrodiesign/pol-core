using Contracts;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

public sealed class OrderPaymentLifecycleTests
{
    private static readonly Guid MerchantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Money Amount = Money.Of(2500m, "THB");
    private static readonly DateTime At = new(2026, 8, 7, 4, 0, 0, DateTimeKind.Utc);

    private static Order NewOrder() => Order.Create(
        MerchantId,
        Amount,
        At,
        OrderLineInputs.OneLine(Amount),
        orderNo: "ORD6900000001",
        paymentChannel: "card");

    private static PaymentPaid Paid(Order order, Guid sessionId, string method = "card") => new(
        Guid.NewGuid(),
        sessionId,
        order.Id,
        MerchantId,
        Amount,
        method,
        "2c2p",
        "charge-1",
        "psp-event-1",
        At.AddMinutes(1));

    [Fact]
    public void OrderStatus_values_match_approved_persistence_contract()
    {
        Assert.Equal(1, (int)OrderStatus.Pending);
        Assert.Equal(2, (int)OrderStatus.Paid);
        Assert.Equal(3, (int)OrderStatus.Failed);
        Assert.Equal(4, (int)OrderStatus.Expired);
        Assert.Equal(5, (int)OrderStatus.Refunded);
        Assert.Equal(6, (int)OrderStatus.Cancelled);
        Assert.Equal(6, Enum.GetNames<OrderStatus>().Length);
    }

    [Fact]
    public void Failed_and_Expired_orders_attach_a_new_attempt_and_return_to_Pending()
    {
        var order = NewOrder();
        var first = Guid.NewGuid();
        order.AttachPaymentAttempt(first);
        Assert.True(order.MarkPaymentFailed(first));

        var second = Guid.NewGuid();
        order.AttachPaymentAttempt(second);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(second, order.PaymentSessionId);
        Assert.Equal("card", order.PaymentChannel);

        Assert.True(order.MarkPaymentExpired(second));
        var third = Guid.NewGuid();
        order.AttachPaymentAttempt(third);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(third, order.PaymentSessionId);
    }

    [Fact]
    public async Task Failed_consumer_ignores_stale_attempt_then_applies_current_attempt_under_lock()
    {
        var order = NewOrder();
        var current = Guid.NewGuid();
        order.AttachPaymentAttempt(current);
        var repository = new FakeOrderRepository(order);
        var unitOfWork = new FakeUnitOfWork();
        var consumer = new OrderPaymentFailedConsumer(repository, unitOfWork);

        await consumer.Handle(new PaymentFailed(
            Guid.NewGuid(), Guid.NewGuid(), order.Id, MerchantId, "psp_failed", At), default);

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(0, unitOfWork.SaveCount);

        var currentEvent = new PaymentFailed(
            Guid.NewGuid(), current, order.Id, MerchantId, "psp_failed", At);
        await consumer.Handle(currentEvent, default);
        await consumer.Handle(currentEvent, default);

        Assert.Equal(OrderStatus.Failed, order.Status);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Equal(3, repository.ForUpdateCalls);
    }

    [Fact]
    public async Task Expired_consumer_correlates_current_attempt_and_replay_is_noop()
    {
        var order = NewOrder();
        var current = Guid.NewGuid();
        order.AttachPaymentAttempt(current);
        var repository = new FakeOrderRepository(order);
        var unitOfWork = new FakeUnitOfWork();
        var consumer = new OrderPaymentExpiredConsumer(repository, unitOfWork);
        var notification = new PaymentExpired(Guid.NewGuid(), current, order.Id, MerchantId, At);

        await consumer.Handle(notification, default);
        await consumer.Handle(notification, default);

        Assert.Equal(OrderStatus.Expired, order.Status);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Equal(2, repository.ForUpdateCalls);
    }

    [Theory]
    [InlineData(OrderStatus.Failed)]
    [InlineData(OrderStatus.Expired)]
    public async Task Verified_late_Paid_wins_from_retryable_order_status(OrderStatus retryableStatus)
    {
        var order = NewOrder();
        var sessionId = Guid.NewGuid();
        order.AttachPaymentAttempt(sessionId);
        if (retryableStatus == OrderStatus.Failed)
            order.MarkPaymentFailed(sessionId);
        else
            order.MarkPaymentExpired(sessionId);

        var unitOfWork = new FakeUnitOfWork();
        await new OrderPaidConsumer(
            new FakeOrderRepository(order), unitOfWork, new FakeDoubleSellAuditor())
            .Handle(Paid(order, sessionId, "card"), default);

        Assert.Equal(OrderStatus.Paid, order.Status);
        Assert.Equal(sessionId, order.PaymentSessionId);
        Assert.Equal("card", order.PaymentChannel);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Paid_from_a_second_session_enters_reconciliation_path()
    {
        var order = NewOrder();
        var firstSession = Guid.NewGuid();
        var consumer = new OrderPaidConsumer(
            new FakeOrderRepository(order), new FakeUnitOfWork(), new FakeDoubleSellAuditor());
        await consumer.Handle(Paid(order, firstSession), default);

        var second = Paid(order, Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<PaymentReconciliationRequiredException>(
            async () => await consumer.Handle(second, default));

        Assert.Contains(second.EventId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains(second.PaymentSessionId.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Equal(firstSession, order.PaymentSessionId);
        Assert.Equal(OrderStatus.Paid, order.Status);
    }
}
