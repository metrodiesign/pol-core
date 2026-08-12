extern alias ApiHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Carts.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Items;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Outbox;
using SharedKernel;
using DirectCoordinator = ApiHost::Api.Orders.OrderCreationCoordinator;
using DirectRequest = ApiHost::Api.Orders.CommitOrderFromCartRequest;
using ProductSnapshot = ApiHost::Api.Orders.ValidatedProductSnapshot;

namespace Hosts.Tests;

public sealed class OrderCreationTransactionTests : IDisposable
{
    private static readonly Guid Merchant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly DateTime Now = new(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CommerceItemMetadata Metadata = new(
        CommerceItemMetadataCodec.InsuranceDocumentSource, "POLICY", "POL-1",
        new DateOnly(2026, 8, 1), new DateOnly(2027, 8, 1));

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public OrderCreationTransactionTests()
    {
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    [Fact]
    public async Task Save_failure_rolls_back_order_items_outbox_and_cart_state()
    {
        var cart = NewCart();
        var existing = Order.Create(
            Merchant, Money.Of(1m, "THB"), Now,
            [new OrderItemInput(1, Money.Of(1m, "THB"), "OTHER", "VMI", null)],
            "ORD6900000001");
        await using (var seed = NewContext())
        {
            seed.AddRange(cart, existing);
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var coordinator = Coordinator(db, "ORD6900000001"); // duplicate OrderNo forces save failure
            var request = Request(cart);

            await Assert.ThrowsAsync<DbUpdateException>(() => coordinator.CommitAsync(request, default));
        }

        await using var verify = NewContext();
        var persistedCart = await verify.Carts.Include(c => c.Items).SingleAsync(c => c.Id == cart.Id);
        Assert.Equal(Carts.Domain.CartStatus.Open, persistedCart.Status);
        Assert.Equal(1, await verify.Orders.CountAsync());
        Assert.Equal(1, await verify.OrderItems.CountAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Stale_captured_version_is_rejected_before_any_order_write()
    {
        var cart = NewCart();
        await using (var seed = NewContext())
        {
            seed.Add(cart);
            await seed.SaveChangesAsync();
        }
        var staleRequest = Request(cart);

        await using (var editor = NewContext())
        {
            var changed = await editor.Carts.Include(c => c.Items).SingleAsync(c => c.Id == cart.Id);
            changed.SetItemQuantity(changed.Items.Single().Id, 2);
            await editor.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var coordinator = Coordinator(db, "ORD6900000002");
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
                coordinator.CommitAsync(staleRequest, default));
        }

        await using var verify = NewContext();
        Assert.Empty(await verify.Orders.ToListAsync());
        Assert.Empty(await verify.OutboxMessages.ToListAsync());
        Assert.Equal(Carts.Domain.CartStatus.Open, (await verify.Carts.SingleAsync(c => c.Id == cart.Id)).Status);
    }

    [Fact]
    public async Task Two_serialized_commits_for_same_cart_have_exactly_one_winner()
    {
        var cart = NewCart();
        var carts = new InMemoryCartStore(cart);
        var orders = new InMemoryOrderStore();
        var outbox = new InMemoryOutbox();
        var unitOfWork = new SerialUnitOfWork();
        var coordinator = new DirectCoordinator(
            null!, null!, carts, orders, new IncrementingOrderNo(), outbox, unitOfWork, new FixedTestClock());
        var request = Request(cart);

        var results = await Task.WhenAll(
            Attempt(() => coordinator.CommitAsync(request, default)),
            Attempt(() => coordinator.CommitAsync(request, default)));

        Assert.Single(results, r => r is null);
        Assert.Single(results, r => r is InvalidOperationException);
        Assert.Single(orders.Orders);
        Assert.Single(outbox.Events);
        Assert.Equal(Carts.Domain.CartStatus.CheckedOut, cart.Status);
    }

    [Fact]
    public async Task Commit_prices_from_cart_and_persists_canonical_payment_channel()
    {
        var cart = NewCart();
        await using (var seed = NewContext())
        {
            seed.Add(cart);
            await seed.SaveChangesAsync();
        }

        await using (var db = NewContext())
            await Coordinator(db, "ORD6900000003").CommitAsync(Request(cart), default);

        await using var verify = NewContext();
        var order = await verify.Orders.SingleAsync();
        Assert.Equal(Money.Of(100m, "THB"), order.Amount);
        Assert.Equal("promptpay", order.PaymentChannel);
    }

    private static async Task<Exception?> Attempt(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) { return ex; }
    }

    private DirectCoordinator Coordinator(MerchantRuntimeDbContext db, string orderNo)
    {
        var actor = new TestActor();
        var clock = new FixedTestClock();
        return new DirectCoordinator(
            null!, null!, new CartRepository(db), new OrderRepository(db), new FixedOrderNo(orderNo),
            new EfOutbox(db, clock, actor),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), clock);
    }

    private static Carts.Domain.Cart NewCart()
    {
        var cart = new Carts.Domain.Cart(Guid.CreateVersion7(), Merchant, "SALE-1", Now);
        cart.AddItem("DOC-1", "SALE-1", "VMI", "ประกันรถยนต์", 1, Money.Of(100m, "THB"), Metadata);
        return cart;
    }

    private static DirectRequest Request(Carts.Domain.Cart cart)
    {
        var item = cart.Items.Single();
        return new DirectRequest(
            Merchant, cart.Id, cart.Version, "SALE-1",
            CustomerContact.Of("Somchai Jaidee", "0812345678", "buyer@example.com"), "promptpay",
            [new ProductSnapshot(item.Id, item.ProductCode, item.VariantCode, item.VariantName, item.Quantity, Metadata)]);
    }

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        new TestActor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    public void Dispose() => _connection.Dispose();

    private sealed class TestActor : IActorContext
    {
        public Guid MerchantId => Merchant;
        public Guid? UserId => Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        public bool HasActor => true;
        public string? SaleCode => "SALE-1";
    }

    private sealed class AllowAllWrites : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class FixedTestClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class FixedOrderNo(string value) : IOrderNoSequence
    {
        public Task<string> NextAsync(CancellationToken cancellationToken) => Task.FromResult(value);
    }

    private sealed class IncrementingOrderNo : IOrderNoSequence
    {
        private int _next;
        public Task<string> NextAsync(CancellationToken cancellationToken) =>
            Task.FromResult($"ORD69{Interlocked.Increment(ref _next):D8}");
    }

    private sealed class InMemoryCartStore(Carts.Domain.Cart cart) : ICartForOrderStore
    {
        public Task<Carts.Domain.Cart?> ReloadTrackedAsync(Guid cartId, CancellationToken cancellationToken) =>
            Task.FromResult<Carts.Domain.Cart?>(cart.Id == cartId ? cart : null);
    }

    private sealed class InMemoryOrderStore : IOrderStore
    {
        public List<Order> Orders { get; } = [];
        public void Add(Order order) => Orders.Add(order);
    }

    private sealed class InMemoryOutbox : IOutbox
    {
        public List<Mediator.INotification> Events { get; } = [];
        public void Enqueue(Mediator.INotification notification) => Events.Add(notification);
    }

    private sealed class SerialUnitOfWork : IUnitOfWork
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(0);

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try { return await operation(cancellationToken); }
            finally { _gate.Release(); }
        }
    }
}
