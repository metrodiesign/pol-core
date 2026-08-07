using BuildingBlocks.Application;
using Mediator;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using SharedKernel;

namespace Orders.Tests;

/// <summary>Shared valid <see cref="OrderItemInput"/> sample for tests that only care about Order's state
/// machine/notification behavior, not insurance-line specifics (insurance-pivot task 3 — <c>Order.Create</c>
/// now always requires at least one line).</summary>
internal static class OrderLineInputs
{
    public static IReadOnlyList<OrderItemInput> OneLine(Money unitPrice) =>
        [new OrderItemInput(
            1, unitPrice,
            "00098-69100/กธ/900001-10", "VMI", "ประกันรถยนต์")];
}

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
    public int ForUpdateCalls { get; private set; }

    public Task<Order?> GetAsync(Guid orderId, CancellationToken ct) =>
        Task.FromResult(_orders.FirstOrDefault(o => o.Id == orderId));

    public Task<Order?> GetForUpdateAsync(Guid orderId, CancellationToken ct)
    {
        ForUpdateCalls++;
        return GetAsync(orderId, ct);
    }

    public IReadOnlyList<OrderStatusTotal> Reconciliation { get; init; } = [];

    public Task<IReadOnlyList<OrderStatusTotal>> GetReconciliationAsync(Guid merchantId, CancellationToken ct) =>
        Task.FromResult(Reconciliation);

    public Task<IReadOnlyList<Order>> ListAsync(Guid merchantId, string? orderNo, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Order>>(
            _orders.Where(o => o.MerchantId == merchantId && (orderNo is null || o.OrderNo == orderNo))
                .OrderByDescending(o => o.CreatedAt).ToList());

    public void Add(Order order) => _orders.Add(order);
}

/// <summary>Hands out ORD69000000NN in call order — the format the real sequence produces, without a DB.</summary>
internal sealed class FakeOrderNoSequence : IOrderNoSequence
{
    private int _next;

    public readonly List<string> Minted = [];

    public Task<string> NextAsync(CancellationToken cancellationToken)
    {
        var orderNo = $"ORD69{++_next:D8}";
        Minted.Add(orderNo);
        return Task.FromResult(orderNo);
    }
}

internal sealed class FakeRevealAuditWriter : IRevealAuditWriter
{
    public readonly List<Guid> Appended = [];
    public bool ShouldThrow { get; init; }

    public Task AppendAsync(Guid orderLineId, Guid merchantId, string actorType, string actorId,
        string correlationId, CancellationToken cancellationToken)
    {
        if (ShouldThrow)
            throw new InvalidOperationException("audit write failed");
        Appended.Add(orderLineId);
        return Task.CompletedTask;
    }
}

/// <summary>Records how many times (and for which orders) the double-sell audit was asked to run. The consumer
/// must call it exactly once on a real transition and never on a replay/mismatch/cancelled/unknown order.</summary>
internal sealed class FakeDoubleSellAuditor : IDoubleSellAuditor
{
    public readonly List<Guid> Reported = [];

    public Task ReportIfDoubleSoldAsync(Guid orderId, CancellationToken cancellationToken)
    {
        Reported.Add(orderId);
        return Task.CompletedTask;
    }
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

/// <summary>Answers a fixed "is a session blocking this order" and records the save count it was asked at —
/// the cancel must only probe AFTER its own UPDATE holds the order row (REQ-4.7's lock ordering).</summary>
internal sealed class FakePaymentSessionProbe(FakeUnitOfWork unitOfWork, bool blocking = false) : IPaymentSessionProbe
{
    public int? SaveCountWhenProbed { get; private set; }

    public Task<bool> HasBlockingSessionAsync(Guid orderId, CancellationToken ct)
    {
        SaveCountWhenProbed ??= unitOfWork.SaveCount;
        return Task.FromResult(blocking);
    }
}

internal sealed class FixedClock : IClock
{
    public DateTime UtcNow { get; init; } = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
}
