using BuildingBlocks.Application;
using Contracts;
using Mediator;

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
/// </list>
/// Depends on the repository port + unit of work, never a DbContext directly.
/// </summary>
public sealed class DocumentPaidOnOrderPaidConsumer : INotificationHandler<OrderPaid>
{
    private readonly IProductRepository _products;
    private readonly IUnitOfWork _unitOfWork;

    public DocumentPaidOnOrderPaidConsumer(IProductRepository products, IUnitOfWork unitOfWork)
    {
        _products = products;
        _unitOfWork = unitOfWork;
    }

    public async ValueTask Handle(OrderPaid notification, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var productId in notification.ProductIds)
        {
            var product = await _products.GetAsync(productId, cancellationToken).ConfigureAwait(false);
            if (product is null)
                continue;

            product.MarkPaid(notification.OccurredAt);
            changed = true;
        }

        if (changed)
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
