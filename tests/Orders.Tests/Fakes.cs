using BuildingBlocks.Application;
using Mediator;
using Orders.Application;
using Orders.Domain;

namespace Orders.Tests;

internal sealed class FakeOutbox : IOutbox
{
    public readonly List<INotification> Enqueued = [];
    public void Enqueue(INotification notification) => Enqueued.Add(notification);
}

internal sealed class FakeNotificationSender : INotificationSender
{
    public readonly List<NotificationMessage> Sent = [];
    public bool ShouldThrow { get; init; }

    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        if (ShouldThrow)
            throw new InvalidOperationException("delivery failed");
        Sent.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class FakeOrderRepository : IOrderRepository
{
    private readonly List<Order> _orders = [];

    public FakeOrderRepository(params Order[] seed) => _orders.AddRange(seed);

    public IReadOnlyList<Order> All => _orders;

    public Task<Order?> GetByPaymentSessionIdAsync(Guid paymentSessionId, CancellationToken ct) =>
        Task.FromResult(_orders.FirstOrDefault(o => o.PaymentSessionId == paymentSessionId));

    public Task<Order?> GetAsync(Guid orderId, CancellationToken ct) =>
        Task.FromResult(_orders.FirstOrDefault(o => o.Id == orderId));

    public Task<Order?> GetByCheckoutSessionIdAsync(Guid checkoutSessionId, CancellationToken ct) =>
        Task.FromResult(_orders.FirstOrDefault(o => o.CheckoutSessionId == checkoutSessionId));

    public IReadOnlyList<OrderStatusTotal> Reconciliation { get; init; } = [];

    public Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid tenantId, CancellationToken ct) =>
        Task.FromResult(Reconciliation);

    public void Add(Order order) => _orders.Add(order);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken ct)
    {
        SaveCount++;
        return Task.FromResult(0);
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) =>
        await operation(ct);
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
}
