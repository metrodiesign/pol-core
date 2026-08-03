using BuildingBlocks.Application;
using Microsoft.Extensions.Logging;
using Products.Application;
using Products.Domain;

namespace Products.Tests;

/// <summary>
/// OrderPaid -> document retirement (REQ-7.2): the consumer marks every matching product PAID in
/// one save, and is defensive against at-least-once delivery — an unknown product id is skipped,
/// never thrown, and does not trigger an empty save. The catalogue is central, so there is no
/// per-merchant skip to assert. Plus the double-sell signal (REQ-5.4): Critical only when the document
/// already belongs to a <em>different</em> order, so an outbox redelivery stays silent.
/// </summary>
public sealed class DocumentPaidOnOrderPaidConsumerTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime PaidAt = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private static Product NewProduct() =>
        Product.Create(
            new ProductInput(ProductGroup.VMI, DocumentType.POLICY,
                "00098-69100/กธ/037674-10", "00098", 2500m, PaymentStatus.UNPAID, null));

    [Fact]
    public async Task It_marks_matching_products_PAID_and_saves_once()
    {
        var a = NewProduct();
        var b = NewProduct();
        var repo = new FakeProductRepository(a, b);
        var uow = new FakeUnitOfWork();
        var orderId = Guid.NewGuid();

        await Consumer(repo, uow)
            .Handle(new Contracts.OrderPaid(Merchant, [a.Id, b.Id], PaidAt, orderId), default);

        Assert.Equal(PaymentStatus.PAID, a.PaymentStatus);
        Assert.Equal(PaidAt, a.PaidDate);
        Assert.Equal(orderId, a.SoldOrderId);
        Assert.Equal(PaymentStatus.PAID, b.PaymentStatus);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task It_skips_an_unknown_product_id_without_saving()
    {
        var uow = new FakeUnitOfWork();

        await Consumer(new FakeProductRepository(), uow)
            .Handle(new Contracts.OrderPaid(Merchant, [Guid.NewGuid()], PaidAt, Guid.NewGuid()), default);

        Assert.Equal(0, uow.SaveCount);
    }

    // REQ-5.4 — the outbox redelivers; the same order arriving twice is the normal case and must not page
    // anyone. A re-mark refreshes the PaidDate, never who bought the document.
    [Fact]
    public async Task A_redelivery_of_the_same_order_is_silent()
    {
        var product = NewProduct();
        var orderId = Guid.NewGuid();
        var logger = new RecordingLogger();
        var consumer = Consumer(new FakeProductRepository(product), new FakeUnitOfWork(), logger);
        var paid = new Contracts.OrderPaid(Merchant, [product.Id], PaidAt, orderId);

        await consumer.Handle(paid, default);
        await consumer.Handle(paid, default);

        Assert.Empty(logger.Critical);
        Assert.Equal(orderId, product.SoldOrderId);
    }

    // REQ-5.4 — the real thing: a second, different order paid for a document already sold. The document
    // keeps its first buyer and someone gets paged.
    [Fact]
    public async Task A_second_order_paying_for_a_sold_document_is_Critical()
    {
        var product = NewProduct();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var logger = new RecordingLogger();
        var consumer = Consumer(new FakeProductRepository(product), new FakeUnitOfWork(), logger);

        await consumer.Handle(new Contracts.OrderPaid(Merchant, [product.Id], PaidAt, first), default);
        await consumer.Handle(new Contracts.OrderPaid(Merchant, [product.Id], PaidAt, second), default);

        var message = Assert.Single(logger.Critical);
        Assert.Contains(first.ToString(), message, StringComparison.Ordinal);
        Assert.Contains(second.ToString(), message, StringComparison.Ordinal);
        Assert.Equal(first, product.SoldOrderId);
    }

    // A payload enqueued before OrderPaid carried the order id: it still retires the document, and it has
    // nothing to compare against, so it neither logs nor claims the document for an empty order.
    [Fact]
    public async Task An_event_without_an_order_id_retires_the_document_quietly()
    {
        var product = NewProduct();
        var logger = new RecordingLogger();

        await Consumer(new FakeProductRepository(product), new FakeUnitOfWork(), logger)
            .Handle(new Contracts.OrderPaid(Merchant, [product.Id], PaidAt), default);

        Assert.Equal(PaymentStatus.PAID, product.PaymentStatus);
        Assert.Null(product.SoldOrderId);
        Assert.Empty(logger.Critical);
    }

    // A document already sold, then an old-shape payload arrives: with no order id there is nothing to
    // compare, so the signal is skipped rather than fired on a guess.
    [Fact]
    public async Task An_event_without_an_order_id_never_reports_a_double_sale()
    {
        var product = NewProduct();
        var logger = new RecordingLogger();
        var consumer = Consumer(new FakeProductRepository(product), new FakeUnitOfWork(), logger);

        await consumer.Handle(new Contracts.OrderPaid(Merchant, [product.Id], PaidAt, Guid.NewGuid()), default);
        await consumer.Handle(new Contracts.OrderPaid(Merchant, [product.Id], PaidAt), default);

        Assert.Empty(logger.Critical);
    }

    private static DocumentPaidOnOrderPaidConsumer Consumer(
        IProductRepository products, IUnitOfWork unitOfWork, RecordingLogger? logger = null) =>
        new(products, unitOfWork, logger ?? new RecordingLogger());

    private sealed class RecordingLogger : ILogger<DocumentPaidOnOrderPaidConsumer>
    {
        public List<string> Critical { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel is LogLevel.Critical)
                Critical.Add(formatter(state, exception));
        }
    }

    private sealed class FakeProductRepository(params Product[] seed) : IProductRepository
    {
        private readonly List<Product> _products = [.. seed];

        public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
            Task.FromResult(_products.FirstOrDefault(p => p.Id == productId));

        public void Add(Product product) => throw new NotSupportedException();
        public Task<IReadOnlyList<Product>> UpsertByDocumentNoAsync(
            IReadOnlyList<ProductInput> inputs, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
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
}
