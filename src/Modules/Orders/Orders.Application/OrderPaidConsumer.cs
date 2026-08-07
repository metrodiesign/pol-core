using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Orders.Domain;

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
    private readonly IDoubleSellAuditor _doubleSellAuditor;

    public OrderPaidConsumer(IOrderRepository orders, IUnitOfWork unitOfWork, IDoubleSellAuditor doubleSellAuditor)
    {
        _orders = orders;
        _unitOfWork = unitOfWork;
        _doubleSellAuditor = doubleSellAuditor;
    }

    public async ValueTask Handle(PaymentPaid notification, CancellationToken cancellationToken)
    {
        var transitioned = await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var order = await _orders.GetForUpdateAsync(notification.OrderId, ct).ConfigureAwait(false);
                if (order is null)
                    return false;

                if (order.MerchantId != notification.MerchantId)
                    throw new InvalidOperationException("Payment event merchant does not match Order merchant.");

                if (order.Status is OrderStatus.Cancelled or OrderStatus.Refunded
                    || order.Status == OrderStatus.Paid && order.PaymentSessionId != notification.PaymentSessionId)
                {
                    throw new PaymentReconciliationRequiredException(
                        notification.EventId, order.Id, notification.PaymentSessionId);
                }

                var changed = order.MarkPaid(
                    notification.PaymentSessionId,
                    notification.Method,
                    notification.Amount,
                    notification.OccurredAt);
                if (changed)
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                return changed;
            },
            cancellationToken).ConfigureAwait(false);

        if (!transitioned)
            return;

        // REQ-5.16/8.2 — this is the one place that sees a REAL transition to Paid (a replay returned above),
        // so it is where "the same document was sold twice" can be reported without paging someone on every
        // outbox redelivery. Deliberately AFTER the save: the auditor reads the committed state, and a report
        // is a report — it must never be able to fail the payment that already happened. There is no
        // Contracts.OrderPaid any more; nothing consumes it since the catalogue mirror was retired (REQ-8.3).
        await _doubleSellAuditor.ReportIfDoubleSoldAsync(notification.OrderId, cancellationToken).ConfigureAwait(false);
    }
}
