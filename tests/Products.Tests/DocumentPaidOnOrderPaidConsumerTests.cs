using BuildingBlocks.Application;
using Products.Application;
using Products.Domain;
using SharedKernel;

namespace Products.Tests;

/// <summary>
/// OrderPaid -> document retirement (REQ-7.2): the consumer marks every matching product PAID + inactive in
/// one save, and is defensive against at-least-once delivery — an unknown product id or one scoped to
/// another merchant is skipped, never thrown, and does not trigger an empty save.
/// </summary>
public sealed class DocumentPaidOnOrderPaidConsumerTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime PaidAt = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private static Product NewProduct(Guid merchantId) =>
        Product.Create(
            new ProductInput(merchantId, ProductGroup.VMI, DocumentType.POLICY,
                "00098-69100/กธ/037674-10", "100", "00098", Money.Of(2500m, "THB")),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

    [Fact]
    public async Task It_marks_matching_products_PAID_and_saves_once()
    {
        var a = NewProduct(Merchant);
        var b = NewProduct(Merchant);
        var repo = new FakeProductRepository(a, b);
        var uow = new FakeUnitOfWork();

        await new DocumentPaidOnOrderPaidConsumer(repo, uow)
            .Handle(new Contracts.OrderPaid(Merchant, [a.Id, b.Id], PaidAt), default);

        Assert.Equal(PaymentStatus.PAID, a.PaymentStatus);
        Assert.False(a.IsActive);
        Assert.Equal(PaidAt, a.PaidDate);
        Assert.Equal(PaymentStatus.PAID, b.PaymentStatus);
        Assert.Equal(1, uow.SaveCount);
    }

    [Fact]
    public async Task It_skips_a_product_belonging_to_another_merchant()
    {
        var foreign = NewProduct(Guid.NewGuid());
        var repo = new FakeProductRepository(foreign);
        var uow = new FakeUnitOfWork();

        await new DocumentPaidOnOrderPaidConsumer(repo, uow)
            .Handle(new Contracts.OrderPaid(Merchant, [foreign.Id], PaidAt), default);

        Assert.Equal(PaymentStatus.UNPAID, foreign.PaymentStatus);
        Assert.True(foreign.IsActive);
        Assert.Equal(0, uow.SaveCount);
    }

    [Fact]
    public async Task It_skips_an_unknown_product_id_without_saving()
    {
        var uow = new FakeUnitOfWork();

        await new DocumentPaidOnOrderPaidConsumer(new FakeProductRepository(), uow)
            .Handle(new Contracts.OrderPaid(Merchant, [Guid.NewGuid()], PaidAt), default);

        Assert.Equal(0, uow.SaveCount);
    }

    private sealed class FakeProductRepository(params Product[] seed) : IProductRepository
    {
        private readonly List<Product> _products = [.. seed];

        public Task<Product?> GetAsync(Guid productId, CancellationToken cancellationToken) =>
            Task.FromResult(_products.FirstOrDefault(p => p.Id == productId));

        public void Add(Product product) => throw new NotSupportedException();
        public Task<IReadOnlyList<Product>> ListByTenantAsync(Guid merchantId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<PagedResult<ProductListItem>> ListAsync(ListProductsQuery query, CancellationToken cancellationToken) =>
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
