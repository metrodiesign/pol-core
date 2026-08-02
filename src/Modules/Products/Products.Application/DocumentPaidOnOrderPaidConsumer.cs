using BuildingBlocks.Application;
using Contracts;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Products.Application;

/// <summary>
/// Consumes the cross-module <see cref="OrderPaid"/> integration event and retires each sold document from
/// the sellable catalog: loads the product, marks it PAID + inactive (<c>Product.MarkPaid</c>), one save at
/// the end. Named to avoid a clash with <c>Orders.Application.OrderPaidConsumer</c> (which consumes
/// <c>PaymentPaid</c> and emits this event). Delivery is at-least-once, so this handler is defensive:
/// <list type="bullet">
///   <item>A product id with no row is skipped — never thrown, or the dispatcher retries a message it can
///   never satisfy. There is no merchant check: the catalogue is central and the id came from an order
///   this platform itself priced.</item>
///   <item>Idempotent on replay: <c>MarkPaid</c> only sets state, so a second delivery is a no-op.</item>
///   <item>Loud only on a real double sale: the document already carries a <c>SoldOrderId</c> from a
///   <em>different</em> order (REQ-5.4). A redelivery of the same order re-marks the same rows and stays
///   silent, which is what keeps this signal worth paging someone about.</item>
/// </list>
/// Depends on the repository port + unit of work, never a DbContext directly.
/// </summary>
public sealed class DocumentPaidOnOrderPaidConsumer : INotificationHandler<OrderPaid>
{
    private readonly IProductRepository _products;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILogger<DocumentPaidOnOrderPaidConsumer> _logger;

    public DocumentPaidOnOrderPaidConsumer(
        IProductRepository products, IUnitOfWork unitOfWork,
        ILogger<DocumentPaidOnOrderPaidConsumer> logger)
    {
        _products = products;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async ValueTask Handle(OrderPaid notification, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var productId in notification.ProductIds)
        {
            var product = await _products.GetAsync(productId, cancellationToken).ConfigureAwait(false);
            if (product is null)
                continue;

            // Guid.Empty = a payload from before OrderPaid carried the order id: there is nothing to
            // compare, so the signal is skipped rather than guessed at.
            if (notification.OrderId != Guid.Empty
                && product.SoldOrderId is { } soldOn && soldOn != notification.OrderId)
                _logger.LogCritical(
                    "Products: document {ProductId} was already sold on order {SoldOrderId}, but order "
                    + "{OrderId} paid for it too — the same document was sold twice.",
                    productId, soldOn, notification.OrderId);

            product.MarkPaid(notification.OccurredAt, notification.OrderId);
            changed = true;
        }

        if (changed)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
