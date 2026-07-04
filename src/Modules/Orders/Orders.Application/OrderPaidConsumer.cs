using BuildingBlocks.Application;
using Contracts;
using Mediator;

namespace Orders.Application;

/// <summary>
/// Consumes the cross-module <see cref="PaymentPaid"/> integration event and fulfils the matching
/// order. Delivery is at-least-once, so this handler is defensive (PLAN decisions #2, #10):
/// <list type="bullet">
///   <item>Loads the order by the event's <c>OrderId</c> (first-class contract field, REQ-2.2) —
///   production orders never carry a <c>PaymentSessionId</c>, so that is not a usable join key
///   (bugfix-order-paid-link F2). If no order is found it returns without throwing (a throw would
///   force the dispatcher to retry a message it can never satisfy).</item>
///   <item>Re-verifies amount AND currency inside <c>Order.MarkPaid</c> — never trusts the event id
///   alone. A mismatch or a cancelled order throws, so the dispatcher parks the message in the DLQ
///   instead of acking a real payment silently (bugfix-order-paid-link F3/F4).</item>
///   <item>Is idempotent: a replayed event whose order is already Paid is a no-op skip.</item>
/// </list>
/// Depends on the repository port + unit of work, never a DbContext directly.
/// </summary>
public sealed class OrderPaidConsumer : INotificationHandler<PaymentPaid>
{
    private readonly IOrderRepository _orders;
    private readonly IUnitOfWork _unitOfWork;

    public OrderPaidConsumer(IOrderRepository orders, IUnitOfWork unitOfWork)
    {
        _orders = orders;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask Handle(PaymentPaid notification, CancellationToken cancellationToken)
    {
        var order = await _orders
            .GetAsync(notification.OrderId, cancellationToken)
            .ConfigureAwait(false);

        // ponytail: at-least-once delivery means a PaymentPaid may arrive for an order this module
        // has no row for (out-of-order/foreign event). Ack and return — never throw, or the
        // dispatcher retries forever. Structured logging is deferred: Orders.Application does not
        // reference Microsoft.Extensions.Logging.Abstractions (csproj is owned by the spine);
        // inject an ILogger here once that package is wired into the project's Directory.*.props.
        if (order is null)
            return;

        // Re-verifies amount + currency; returns false (no transition) if the order is already Paid,
        // so a replayed event is an idempotent no-op skip.
        var transitioned = order.MarkPaid(notification.Amount, notification.OccurredAt);

        // ponytail: replay (already Paid) is a deliberate skip — see logging note above.
        if (!transitioned)
            return;

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
