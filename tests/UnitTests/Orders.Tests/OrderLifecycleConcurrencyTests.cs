using BuildingBlocks.Application;
using Contracts;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

public sealed class OrderLifecycleConcurrencyTests
{
    [Fact]
    public async Task Concurrent_cancel_and_paid_event_serialize_to_one_terminal_winner()
    {
        var merchantId = Guid.NewGuid();
        var amount = Money.Of(100m, "THB");
        var order = Order.Create(
            merchantId,
            amount,
            DateTime.UtcNow,
            OrderLineInputs.OneLine(amount),
            orderNo: "ORD6900000001",
            paymentChannel: "card");
        var paymentSessionId = Guid.NewGuid();
        order.AttachPaymentAttempt(paymentSessionId);

        var repository = new SharedRepository(order);
        var unitOfWork = new SerialUnitOfWork();
        var cancel = new CancelOrderHandler(repository, new NonBlockingSessionProbe(), unitOfWork);
        var paid = new OrderPaidConsumer(repository, unitOfWork, new FakeDoubleSellAuditor());
        var paidEvent = new PaymentPaid(
            Guid.NewGuid(), paymentSessionId, order.Id, merchantId, amount, "card",
            "2c2p", "charge-1", "psp-event-1", DateTime.UtcNow);

        var results = await Task.WhenAll(
            Attempt(async () => await cancel.Handle(new CancelOrderCommand(order.Id), default)),
            Attempt(async () => await paid.Handle(paidEvent, default)));

        Assert.Single(results, x => x is null);
        Assert.Single(results, x => x is ConflictException or PaymentReconciliationRequiredException);
        Assert.True(order.Status is OrderStatus.Paid or OrderStatus.Cancelled);
        Assert.Equal(2, repository.LockedReads);
    }

    private static async Task<Exception?> Attempt(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class SharedRepository(Order order) : IOrderRepository
    {
        public int LockedReads { get; private set; }
        public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<Order?>(order.Id == orderId ? order : null);
        public Task<Order?> GetForUpdateAsync(Guid orderId, CancellationToken cancellationToken)
        {
            LockedReads++;
            return GetAsync(orderId, cancellationToken);
        }
        public Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(
            Guid merchantId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<OrderStatusTotal>>([]);
        public Task<PagedResult<Order>> ListAsync(
            Guid merchantId, PagedQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new PagedResult<Order>([order], query.Page, query.Limit, 1));
        public void Add(Order value) => throw new NotSupportedException();
    }

    private sealed class NonBlockingSessionProbe : IPaymentSessionProbe
    {
        public Task<bool> HasBlockingSessionAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private sealed class SerialUnitOfWork : IUnitOfWork
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(1);
        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                return await operation(cancellationToken);
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
