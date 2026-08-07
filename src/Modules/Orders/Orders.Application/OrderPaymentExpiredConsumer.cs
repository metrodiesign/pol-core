using BuildingBlocks.Application;
using Contracts;
using Mediator;

namespace Orders.Application;

/// <summary>Applies only current-attempt expiry; stale/replayed events acknowledge without mutation.</summary>
public sealed class OrderPaymentExpiredConsumer : INotificationHandler<PaymentExpired>
{
    private readonly IOrderRepository _orders;
    private readonly IUnitOfWork _unitOfWork;

    public OrderPaymentExpiredConsumer(IOrderRepository orders, IUnitOfWork unitOfWork)
    {
        _orders = orders;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask Handle(PaymentExpired notification, CancellationToken cancellationToken)
    {
        await _unitOfWork.ExecuteInTransactionAsync(
            async ct =>
            {
                var order = await _orders.GetForUpdateAsync(notification.OrderId, ct).ConfigureAwait(false);
                if (order is null)
                    return false;
                if (order.MerchantId != notification.MerchantId)
                    throw new InvalidOperationException("Payment event merchant does not match Order merchant.");

                var changed = order.MarkPaymentExpired(notification.PaymentSessionId);
                if (changed)
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
                return changed;
            },
            cancellationToken).ConfigureAwait(false);
    }
}
