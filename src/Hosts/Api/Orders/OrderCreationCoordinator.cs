using BuildingBlocks.Application;
using Carts.Application;
using Contracts;
using Mediator;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using Products.Application;
using Products.Domain;
using Payments.Domain;
using SharedKernel;

namespace Api.Orders;

public sealed record ValidatedProductSnapshot(
    Guid CartItemId,
    string ProductCode,
    string VariantCode,
    string? VariantName,
    int Quantity,
    CommerceItemMetadata Metadata);

public sealed record CommitOrderFromCartRequest(
    Guid MerchantId,
    Guid CartId,
    int ExpectedCartVersion,
    string SaleCode,
    CustomerContact Customer,
    string PaymentMethod,
    IReadOnlyList<ValidatedProductSnapshot> Products,
    Guid? OriginatorId = null);

public sealed record DirectOrderResult(Guid OrderId, string OrderNo, string Status, Money Amount);

public interface IOrderCreationTransactionCoordinator
{
    Task<DirectOrderResult> CommitAsync(
        CommitOrderFromCartRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Host composition seam: validates live product state before opening a database transaction, then commits
/// owner aggregates and notification outbox through one shared MerchantRuntime unit of work.
/// </summary>
internal sealed class OrderCreationCoordinator : IOrderCreationTransactionCoordinator
{
    private readonly IMediator _mediator;
    private readonly IDocumentSaleProbe _documentSales;
    private readonly ICartForOrderStore _carts;
    private readonly IOrderStore _orders;
    private readonly IOrderNoSequence _orderNumbers;
    private readonly IOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public OrderCreationCoordinator(
        IMediator mediator,
        IDocumentSaleProbe documentSales,
        ICartForOrderStore carts,
        IOrderStore orders,
        IOrderNoSequence orderNumbers,
        IOutbox outbox,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _mediator = mediator;
        _documentSales = documentSales;
        _carts = carts;
        _orders = orders;
        _orderNumbers = orderNumbers;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<DirectOrderResult> CreateAsync(
        Guid merchantId,
        Guid cartId,
        string saleCode,
        CustomerContact customer,
        string paymentMethod,
        CancellationToken cancellationToken,
        Guid? originatorId = null)
    {
        var request = await PrepareAsync(
            merchantId, cartId, saleCode, customer, paymentMethod, cancellationToken, originatorId)
            .ConfigureAwait(false);
        return await CommitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommitOrderFromCartRequest> PrepareAsync(
        Guid merchantId,
        Guid cartId,
        string saleCode,
        CustomerContact customer,
        string paymentMethod,
        CancellationToken cancellationToken,
        Guid? originatorId = null)
    {
        paymentMethod = PaymentMethods.Normalize(paymentMethod);
        var cart = await _mediator.Send(new GetCartQuery(cartId, merchantId), cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException("Cart was not found.");
        if (cart.Status != nameof(Carts.Domain.CartStatus.Open))
            throw new InvalidOperationException("Cart is not open.");
        if (cart.Items.Count == 0 || cart.Subtotal is null)
            throw new ArgumentException("Cannot create an order from an empty cart.", nameof(cartId));
        if (originatorId != cart.OriginatorId)
            throw new ConflictException("Cart originator does not match the requested originator.", "state_conflict");

        var products = new List<ValidatedProductSnapshot>(cart.Items.Count);
        foreach (var line in cart.Items)
        {
            if (!Enum.GetNames<ProductGroup>().Contains(line.VariantCode, StringComparer.Ordinal)
                || !Enum.TryParse<ProductGroup>(line.VariantCode, out var productGroup))
                throw new InvalidOperationException("Cart product is no longer available.");

            var document = await _mediator.Send(
                    new LookupDocumentQuery(line.ProductCode, productGroup, saleCode), cancellationToken)
                .ConfigureAwait(false);
            if (document is null || document.PaymentStatus == PaymentStatus.PAID)
                throw new InvalidOperationException("Cart product is no longer available.");

            var variantCode = document.ProductGroup.ToString();
            products.Add(new ValidatedProductSnapshot(
                line.ItemId,
                document.DocumentNo,
                variantCode,
                string.IsNullOrWhiteSpace(document.ShowName) ? variantCode : document.ShowName,
                line.Quantity,
                new CommerceItemMetadata(
                    CommerceItemMetadataCodec.InsuranceDocumentSource,
                    document.DocumentType.ToString(),
                    document.PolicyNumber,
                    document.StartDate is { } start ? DateOnly.FromDateTime(start) : null,
                    document.EndDate is { } end ? DateOnly.FromDateTime(end) : null)));
        }

        var saleStatuses = await _documentSales.ProbeAsync(
            products.Select(p => new DocumentKey(p.ProductCode, p.VariantCode)).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        if (saleStatuses.Count > 0)
            throw new InvalidOperationException("Cart product is no longer available.");

        return new CommitOrderFromCartRequest(
            merchantId, cartId, cart.Version, saleCode, customer, paymentMethod, products, cart.OriginatorId);
    }

    public Task<DirectOrderResult> CommitAsync(
        CommitOrderFromCartRequest request,
        CancellationToken cancellationToken) =>
        _unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var cart = await _carts.ReloadTrackedAsync(request.CartId, ct).ConfigureAwait(false)
                ?? throw new NotFoundException("Cart was not found.");
            if (cart.MerchantId != request.MerchantId)
                throw new NotFoundException("Cart was not found.");
            if (cart.Status != Carts.Domain.CartStatus.Open)
                throw new InvalidOperationException("Cart is not open.");
            if (cart.Version != request.ExpectedCartVersion)
                throw new ConcurrencyConflictException("Cart changed after validation.");
            if (cart.Items.Count != request.Products.Count)
                throw new ConcurrencyConflictException("Cart lines changed after validation.");

            var snapshots = request.Products.ToDictionary(p => p.CartItemId);
            foreach (var line in cart.Items)
            {
                if (!snapshots.TryGetValue(line.Id, out var snapshot)
                    || !string.Equals(line.ProductCode, snapshot.ProductCode, StringComparison.Ordinal)
                    || !string.Equals(line.VariantCode, snapshot.VariantCode, StringComparison.Ordinal)
                    || line.Quantity != snapshot.Quantity)
                    throw new ConcurrencyConflictException("Cart lines changed after validation.");
            }

            var total = cart.Subtotal
                ?? throw new ArgumentException("Cannot create an order from an empty cart.", nameof(request));
            var paymentMethod = PaymentMethods.Normalize(request.PaymentMethod);

            var inputs = cart.Items.Select(line =>
            {
                var snapshot = snapshots[line.Id];
                return new OrderItemInput(
                    line.Quantity,
                    line.UnitPrice,
                    snapshot.ProductCode,
                    snapshot.VariantCode,
                    snapshot.VariantName,
                    Money.Zero(line.UnitPrice.Currency),
                    snapshot.Metadata);
            }).ToList();

            var orderNo = await _orderNumbers.NextAsync(ct).ConfigureAwait(false);
            var order = Order.Create(
                request.MerchantId,
                total,
                _clock.UtcNow,
                inputs,
                orderNo,
                notificationRecipient: request.Customer.NotificationRecipient,
                paymentChannel: paymentMethod,
                customer: request.Customer,
                saleCode: request.SaleCode,
                originatorId: request.OriginatorId);
            _orders.Add(order);

            _outbox.Enqueue(new CustomerOrderNotification(
                order.MerchantId,
                order.Id,
                order.NotificationRecipient!,
                order.SummaryToken,
                _clock.UtcNow,
                order.OrderNo));
            cart.MarkCheckedOut();

            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
            return new DirectOrderResult(order.Id, order.OrderNo, nameof(OrderStatus.Pending), order.Amount);
        }, cancellationToken);
}
