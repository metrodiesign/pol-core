extern alias ApiHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Carts.Application;
using Checkouts.Application;
using Checkouts.Domain.Items;
using Contracts;
using Mediator;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Carts;
using Persistence.MerchantRuntime.Checkouts;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Orders.Items;
using Persistence.MerchantRuntime.Outbox;
using Persistence.MerchantRuntime.Products;
using Products.Application;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;
using OrderItemRevealAudit = Orders.Domain.Items.RevealAudit;

namespace Hosts.Tests;

/// <summary>
/// insurance-pivot task 3/4 — the full happy path design.md's Testing Strategy names: an insurance Product in
/// a cart -&gt; start checkout with a per-line insured person (server-sourced terms) -&gt; confirm -&gt; dispatch
/// -&gt; Order + OrderItem created -&gt; MarkPaid, THEN the 3 REQ-7.4/7.5 read surfaces on top of that same paid
/// order: masked list, full+audited detail, and the anonymous customer summary. Every step calls the REAL
/// Application handler backed by the REAL EF repository/unit-of-work on SQLite in-memory — Product/Checkout/
/// list/detail writes run under the REAL Api-host <c>MerchantRequestWriteAuthorizer</c> (reached via the
/// ApiHost:: alias), the CheckoutConfirmedConsumer dispatch step runs under the REAL background-dispatch
/// <c>Api.BackgroundDispatch.WorkerWriteAuthorizer</c> (same alias) — mirroring exactly which write floor
/// each execution scope uses in production. The `/checkouts` endpoint's own trust-boundary logic (Program.cs) is reproduced inline here
/// since that logic is a minimal-API lambda, not a separately callable unit; the outbox dispatch loop itself
/// is bypassed the same way task 0 already justifies (SQL-Server-only lease SQL, orthogonal to what this
/// proves) by capturing the enqueued notification and calling the consumer directly.
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

    /// <summary>Frozen "today" for the ProductRepository search window (REQ-6.1) so the document created below
    /// stays inside it whatever the wall clock says.</summary>
    private static readonly DateTime Today = new(2026, 7, 30, 9, 0, 0, DateTimeKind.Utc);

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

    // Mirrors the background-dispatch scope: WorkerWriteAuthorizer (stateless, never compares the actor).
    private MerchantRuntimeDbContext WorkerContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(MerchantA), new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer(), NoOpSecurityTelemetry.Instance);

    /// <summary>Product -&gt; cart -&gt; checkout -&gt; confirm -&gt; real Worker dispatch -&gt; paid Order, one insured
    /// person, "Somchai"/"Jaidee"/"1234567890123"/<see cref="Dob"/>. Shared by every test below so each one
    /// only has to prove its own read surface, not re-derive the write path task 3 already proves.</summary>
    private async Task<(Guid ProductId, Guid CheckoutSessionId, Guid OrderId, string SummaryToken)> CreatePaidOrderAsync()
    {
        Guid productId;
        using (var db = ApiContext())
        {
            var handler = new CreateProductHandler(
                NewProductRepository(db),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance));
            productId = await handler.Handle(
                new CreateProductCommand(new Products.Domain.ProductInput(
                    Products.Domain.ProductGroup.VMI, Products.Domain.DocumentType.POLICY,
                    "00098-69100/กธ/037674-10", "00098", 2500m,
                    StartDate: new DateTime(2026, 7, 1), EndDate: new DateTime(2026, 7, 31),
                    ShowName: "Somchai Jaidee", BrokerName: "Muang Thai Insurance")),
                CancellationToken.None);
        }

        Guid cartId;
        using (var db = ApiContext())
        {
            var product = await new GetProductByIdHandler(NewProductRepository(db))
                .Handle(new GetProductByIdQuery(productId), CancellationToken.None);

            var cart = new Carts.Domain.Cart(Guid.CreateVersion7(), MerchantA, DateTime.UtcNow);
            // The currency is minted at this boundary, exactly as Program.cs's cart add-item does (REQ-8.4).
            cart.AddItem(productId, 1, Money.Of(product!.TotalPremium, "THB"));
            db.Add(cart);
            await db.SaveChangesAsync();
            cartId = cart.Id;
        }

        Guid checkoutSessionId;
        using (var db = ApiContext())
        {
            var cart = await new GetCartHandler(new CartRepository(db))
                .Handle(new GetCartQuery(cartId, MerchantA), CancellationToken.None);
            var item = Assert.Single(cart!.Items);
            var product = await new GetProductByIdHandler(NewProductRepository(db))
                .Handle(new GetProductByIdQuery(item.ProductId), CancellationToken.None);

            var items = new List<CheckoutItemInput>
            {
                new(item.ProductId, item.Quantity, item.UnitPrice,
                    product!.DocumentNo, product.ProductGroup.ToString(), product.DocumentType.ToString(),
                    product.PolicyNumber, product.StartDate, product.EndDate,
                    "Somchai", "Jaidee", "1234567890123", Dob),
            };

            var handler = new StartCheckoutHandler(
                new CheckoutRepository(db), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());
            var result = await handler.Handle(
                new StartCheckoutCommand(MerchantA, cartId, cart.Subtotal!.Value, items), CancellationToken.None);
            checkoutSessionId = result.CheckoutSessionId;
        }

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

        using (var db = WorkerContext())
        {
            var consumer = new CheckoutConfirmedConsumer(
                new OrderRepository(db), new EfOutbox(db, new SystemClock(), FakeActor.For(MerchantA)),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());
            await consumer.Handle(confirmed, CancellationToken.None);
        }

        Guid orderId;
        string summaryToken;
        Contracts.OrderPaid orderPaid;
        using (var db = WorkerContext())
        {
            var order = await db.Set<OrderAggregate>().Include(o => o.Items).SingleAsync(o => o.CheckoutSessionId == checkoutSessionId);
            var outbox = new CapturingOutbox(new EfOutbox(db, new SystemClock(), FakeActor.For(MerchantA)));
            var consumer = new OrderPaidConsumer(
                new OrderRepository(db), outbox, new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance));
            // Real PaymentPaid -> OrderPaidConsumer path (REQ-7.1): MarkPaid + enqueue OrderPaid in one UoW.
            var paymentPaid = new PaymentPaid(
                Guid.NewGuid(), order.Id, MerchantA, order.Amount, "2c2p", "chg_e2e", "evt_e2e", DateTime.UtcNow);
            await consumer.Handle(paymentPaid, CancellationToken.None);
            orderId = order.Id;
            summaryToken = order.SummaryToken;
            orderPaid = Assert.IsType<Contracts.OrderPaid>(outbox.Captured);
        }

        // Products consumer retires each sold document (REQ-7.2), same drain scope / real Worker write floor.
        using (var db = WorkerContext())
        {
            var consumer = new DocumentPaidOnOrderPaidConsumer(
                NewProductRepository(db),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance));
            await consumer.Handle(orderPaid, CancellationToken.None);
        }

        return (productId, checkoutSessionId, orderId, summaryToken);
    }

    [Fact]
    public async Task Product_to_cart_to_checkout_to_paid_order_survives_the_real_write_floor_end_to_end()
    {
        var (productId, checkoutSessionId, _, _) = await CreatePaidOrderAsync();

        using var verify = WorkerContext();
        var paid = await verify.Orders.Include(o => o.Items).SingleAsync(o => o.CheckoutSessionId == checkoutSessionId);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        var item = Assert.Single(paid.Items);
        Assert.Equal(productId, item.ProductId);
        // The line snapshots the document itself, exactly as the Product was created above.
        Assert.Equal("00098-69100/กธ/037674-10", item.DocumentNo);
        Assert.Equal("VMI", item.ProductGroup);
        Assert.Equal("POLICY", item.DocumentType);
        Assert.Null(item.PolicyNumber);
        Assert.Equal(new DateTime(2026, 7, 1), item.StartDate);
        Assert.Equal(new DateTime(2026, 7, 31), item.EndDate);
        Assert.Equal("Somchai", item.InsuredFirstName);
        Assert.Equal("1234567890123", item.InsuredIdNumber);
    }

    // REQ-2.1/7.2 — a paid order retires each sold document: PAID is the whole "not sellable" axis now that
    // IsActive is gone, and PaymentStatus is exactly the value both host gates read (cart add-item -> 400,
    // checkout start -> 409). The HTTP status codes themselves live in minimal-API lambdas and are proven by
    // the dev-DB E2E of REQ-12.6, not here.
    [Fact]
    public async Task Paid_order_marks_the_product_PAID_so_it_can_no_longer_be_sold()
    {
        var (productId, _, _, _) = await CreatePaidOrderAsync();

        using var db = ApiContext();
        var product = await db.Set<Products.Domain.Product>().SingleAsync(p => p.Id == productId);
        Assert.Equal(Products.Domain.PaymentStatus.PAID, product.PaymentStatus);

        // What the gates see: the read used by both of them reports a non-UNPAID document.
        var read = await new GetProductByIdHandler(NewProductRepository(db))
            .Handle(new GetProductByIdQuery(productId), CancellationToken.None);
        Assert.NotNull(read);
        Assert.NotEqual(Products.Domain.PaymentStatus.UNPAID, read!.PaymentStatus);

        // ...and it has left the default (UNPAID-only) list surface (REQ-3.1).
        var listed = await new ListProductsHandler(NewProductRepository(db)).Handle(
            new ListProductsQuery
            {
                ProductFilters = new ProductFilterDto { SaleCode = "00098" },
            },
            CancellationToken.None);
        Assert.Empty(listed.Items);
    }

    // REQ-7.4 — merchant-authenticated list surface: masked, no audit trail written.
    [Fact]
    public async Task List_endpoint_masks_InsuredIdNumber_and_writes_no_reveal_audit()
    {
        await CreatePaidOrderAsync();

        using var db = ApiContext();
        var result = await new GetOrdersHandler(new OrderRepository(db)).Handle(new GetOrdersQuery(MerchantA), CancellationToken.None);

        var line = Assert.Single(Assert.Single(result.Orders).Lines);
        Assert.Equal("****0123", line.MaskedInsuredIdNumber);
        Assert.Equal("Somchai", line.InsuredFirstName);

        Assert.Empty(await db.OrderItemRevealAudits.ToListAsync());
    }

    // REQ-7.4/7.5 — merchant-authenticated detail surface: full value, exactly one audit row per line
    // returned, real Api-host write floor (MerchantRequestWriteAuthorizer) + real AppendOnlyDescriptor table.
    [Fact]
    public async Task Detail_endpoint_reveals_full_InsuredIdNumber_and_writes_one_reveal_audit_row()
    {
        var (_, _, orderId, _) = await CreatePaidOrderAsync();

        using (var db = ApiContext())
        {
            var handler = new GetOrderDetailHandler(new OrderRepository(db), new RevealAuditWriter(db), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance));
            var result = await handler.Handle(new GetOrderDetailCommand(MerchantA, orderId, "merchant-user", "user-1"), CancellationToken.None);

            var line = Assert.Single(result.Lines);
            Assert.Equal("1234567890123", line.InsuredIdNumber);
        }

        using var verify = ApiContext();
        var audit = Assert.Single(await verify.OrderItemRevealAudits.ToListAsync());
        Assert.Equal(MerchantA, audit.MerchantId);
        Assert.Equal("merchant-user", audit.ActorType);
        Assert.Equal("user-1", audit.ActorId);
    }

    // REQ-7.5 fail-closed — a reveal that cannot be proven audited must not happen: no PII is returned and
    // no partial audit row is left behind when the audit write fails.
    [Fact]
    public async Task Detail_endpoint_fails_closed_when_the_reveal_audit_write_fails()
    {
        var (_, _, orderId, _) = await CreatePaidOrderAsync();

        using (var db = ApiContext())
        {
            var handler = new GetOrderDetailHandler(new OrderRepository(db), new ThrowingRevealAuditWriter(), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.Handle(new GetOrderDetailCommand(MerchantA, orderId, "merchant-user", "user-1"), CancellationToken.None).AsTask());
        }

        using var verify = ApiContext();
        Assert.Empty(await verify.OrderItemRevealAudits.ToListAsync());
    }

    // NOTE: OrderSummaryReader.GetByTokenAsync (the customer-summary surface) is deliberately NOT exercised
    // here — its 2 raw SqlQueryRaw queries use SQL-Server-only T-SQL (`SELECT TOP 1 ... FROM shop.Orders`)
    // that predates this task and does not run against SQLite (`SQLite Error 1: 'near "1": syntax error'`,
    // confirmed by trying). Its masking behavior is proven at Integration.Tests level instead — see
    // OrderSummaryReaderIntegrationTests.cs, run against the real pol-db container.

    private static ProductRepository NewProductRepository(MerchantRuntimeDbContext db) =>
        new(db, new FixedClock(Today));

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
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

    private sealed class ThrowingRevealAuditWriter : IRevealAuditWriter
    {
        public Task AppendAsync(Guid orderLineId, Guid merchantId, string actorType, string actorId,
            string correlationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("audit write failed");
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
