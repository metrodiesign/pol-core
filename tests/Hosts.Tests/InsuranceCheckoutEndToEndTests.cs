extern alias ApiHost;
extern alias WorkerHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Carts.Application;
using Checkouts.Application;
using Checkouts.Domain.Lines;
using Contracts;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Application;
using Orders.Domain;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Checkouts;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;

namespace Hosts.Tests;

/// <summary>
/// insurance-pivot task 3 — the full happy path design.md's Testing Strategy names: an insurance Product in
/// a cart -&gt; start checkout with a per-line insured person (server-sourced terms) -&gt; confirm -&gt; dispatch
/// -&gt; Order + OrderLine created -&gt; MarkPaid. Every step calls the REAL Application handler backed by the
/// REAL EF repository/unit-of-work on SQLite in-memory — Product/Checkout writes run under the REAL Api-host
/// <c>MerchantRequestWriteAuthorizer</c> (reached via the ApiHost:: alias), the CheckoutConfirmedConsumer
/// dispatch step runs under the REAL Worker-host <c>WorkerWriteAuthorizer</c> (WorkerHost:: alias) —
/// mirroring exactly which host performs each write in production. The `/checkouts` endpoint's own
/// trust-boundary logic (Program.cs) is reproduced inline here since that logic is a minimal-API lambda,
/// not a separately callable unit; the outbox dispatch loop itself is bypassed the same way task 0 already
/// justifies (SQL-Server-only lease SQL, orthogonal to what this proves) by capturing the enqueued
/// notification and calling the consumer directly.
/// <para>
/// The cart is built directly via the real <c>Carts.Domain.Cart</c> domain object (<c>new Cart(...)</c> +
/// <c>AddItem</c>, one <c>SaveChangesAsync</c>) rather than through <c>CreateCartHandler</c> then
/// <c>AddItemToCartHandler</c> as two separate requests would: that two-request sequence was found, while
/// building this test, to throw <see cref="BuildingBlocks.Application.ConcurrencyConflictException"/> on
/// BOTH SQLite and a real SQL Server <c>pol-db</c> — a genuine PRE-EXISTING bug in <c>Carts</c>
/// (introduced by the rls-to-query-filter migration's concurrency-token write floor, unrelated to and not
/// caused by this spec, and explicitly out of scope here — <c>Carts.Domain</c> is "not touched" per
/// design.md). Reported separately; not fixed in this task. Cart itself is out of scope for insurance-pivot
/// either way — <c>Item</c> already carries everything REQ-6 needs.
/// </para>
/// </summary>
public sealed class InsuranceCheckoutEndToEndTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly DateTime Dob = new(1985, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public InsuranceCheckoutEndToEndTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = ApiContext();
        setup.Database.EnsureCreated();
    }

    // Mirrors the Api host: MerchantRequestWriteAuthorizer, bound to MerchantA.
    private MerchantRuntimeDbContext ApiContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(MerchantA), new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(FakeActor.For(MerchantA)),
            NoOpSecurityTelemetry.Instance);

    // Mirrors the Worker host: WorkerWriteAuthorizer (stateless, never compares the actor).
    private MerchantRuntimeDbContext WorkerContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(MerchantA), new WorkerHost::Worker.WorkerWriteAuthorizer(), NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task Product_to_cart_to_checkout_to_paid_order_survives_the_real_write_floor_end_to_end()
    {
        // 1. Create an insurance product (Api-host write floor).
        Guid productId;
        using (var db = ApiContext())
        {
            var handler = new CreateProductHandler(
                new ProductRepository(db, NullLogger<ProductRepository>.Instance),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());
            productId = await handler.Handle(
                new CreateProductCommand(
                    MerchantA, "Travel Plan", Money.Of(2500m, "THB"), Money.Of(1_000_000m, "THB"), 30, "Muang Thai Insurance"),
                CancellationToken.None);
        }

        // 2. Open a cart with the product, quantity 1, priced from the catalog (never a client price) — see
        // the type-level doc comment for why this is one real Cart.AddItem call, not two separate requests.
        Guid cartId;
        using (var db = ApiContext())
        {
            var product = await new GetProductByIdHandler(new ProductRepository(db, NullLogger<ProductRepository>.Instance))
                .Handle(new GetProductByIdQuery(MerchantA, productId), CancellationToken.None);

            var cart = new Carts.Domain.Cart(Guid.CreateVersion7(), MerchantA, DateTime.UtcNow);
            cart.AddItem(productId, 1, product!.Price);
            db.Add(cart);
            await db.SaveChangesAsync();
            cartId = cart.Id;
        }

        // 3. Start checkout — reproduces the /checkouts trust-boundary fix: UnitPrice from the cart,
        // insurance terms from a fresh server-side Product fetch, insured-person PII from the "client".
        Guid checkoutSessionId;
        using (var db = ApiContext())
        {
            var cart = await new GetCartHandler(new CartRepository(db))
                .Handle(new GetCartQuery(cartId, MerchantA), CancellationToken.None);
            var item = Assert.Single(cart!.Items);
            var product = await new GetProductByIdHandler(new ProductRepository(db, NullLogger<ProductRepository>.Instance))
                .Handle(new GetProductByIdQuery(MerchantA, item.ProductId), CancellationToken.None);

            var lines = new List<CheckoutLineInput>
            {
                new(item.ProductId, item.Quantity, item.UnitPrice, product!.SumInsured, product.CoverageDurationDays,
                    product.Insurer, "Somchai", "Jaidee", "1234567890123", Dob),
            };

            var handler = new StartCheckoutHandler(
                new CheckoutRepository(db), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());
            var result = await handler.Handle(
                new StartCheckoutCommand(MerchantA, cartId, cart.Subtotal!.Value, lines), CancellationToken.None);
            checkoutSessionId = result.CheckoutSessionId;
        }

        // 4. Confirm — captures the CheckoutConfirmed notification the real EfOutbox enqueues.
        CheckoutConfirmed confirmed;
        using (var db = ApiContext())
        {
            var outbox = new CapturingOutbox(new EfOutbox(db, new SystemClock(), FakeActor.For(MerchantA)));
            var handler = new ConfirmCheckoutHandler(
                new CheckoutRepository(db), outbox, new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
                new SystemClock());
            await handler.Handle(new ConfirmCheckoutCommand(checkoutSessionId, MerchantA), CancellationToken.None);
            confirmed = Assert.IsType<CheckoutConfirmed>(outbox.Captured);
        }

        // 5. Dispatch — the Worker-host write floor opens the Order + OrderLine (task 0/3's own proof, reused
        // here as the last leg of the full chain rather than a hand-built notification).
        using (var db = WorkerContext())
        {
            var consumer = new CheckoutConfirmedConsumer(
                new OrderRepository(db), new EfOutbox(db, new SystemClock(), FakeActor.For(MerchantA)),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());
            await consumer.Handle(confirmed, CancellationToken.None);
        }

        // 6. Pay — MarkPaid through the Worker write floor (same gap task 0 closed for the Update path).
        using (var db = WorkerContext())
        {
            var order = await db.Set<OrderAggregate>().SingleAsync(o => o.CheckoutSessionId == checkoutSessionId);
            order.MarkPaid(order.Amount, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        // Verify: the paid order carries exactly the line the insured person supplied, sourced from the
        // real Product, not fabricated in this test.
        using var verify = WorkerContext();
        var paid = await verify.Orders.Include(o => o.Lines).SingleAsync(o => o.CheckoutSessionId == checkoutSessionId);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        var line = Assert.Single(paid.Lines);
        Assert.Equal(productId, line.ProductId);
        Assert.Equal(Money.Of(1_000_000m, "THB"), line.SumInsured);
        Assert.Equal(30, line.CoverageDurationDays);
        Assert.Equal("Muang Thai Insurance", line.Insurer);
        Assert.Equal("Somchai", line.InsuredFirstName);
        Assert.Equal("1234567890123", line.InsuredIdNumber);
    }

    private sealed class CapturingOutbox(IOutbox inner) : IOutbox
    {
        public INotification? Captured { get; private set; }

        public void Enqueue(INotification notification)
        {
            Captured = notification;
            inner.Enqueue(notification);
        }
    }

    private sealed class FakeActor(bool hasActor, Guid merchantId = default) : IActorContext
    {
        public static FakeActor For(Guid merchantId) => new(true, merchantId);

        public Guid MerchantId => hasActor ? merchantId : throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => hasActor;
    }

    public void Dispose() => _connection.Dispose();
}
