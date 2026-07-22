extern alias ApiHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain;
using Orders.Domain.Lines;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders;
using Persistence.MerchantRuntime.Outbox;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;
using OrderLine = Orders.Domain.Lines.Line;

namespace Hosts.Tests;

/// <summary>
/// insurance-pivot task 0/3: proves the CheckoutConfirmed -&gt; Order (+ OrderLine, task 3) write survives
/// the REAL write floor — the actual <see cref="CheckoutConfirmedConsumer"/>, the actual EF repository/
/// unit-of-work/outbox (Persistence.MerchantRuntime, reached here via the InternalsVisibleTo grant that
/// project now gives Hosts.Tests), and the REAL <c>Api.BackgroundDispatch.WorkerWriteAuthorizer</c> (via the
/// ApiHost:: alias) — none of the fakes `Orders.Tests/CheckoutConfirmedConsumerTests.cs` uses. SQLite in-memory
/// stands in for SQL Server, the same substitution `Architecture.Tests/WriteFloorTests.cs` already uses to
/// prove the write-guard mechanics; the dispatcher's SQL-Server-only lease/poll loop (`OutboxDispatcher`) is
/// orthogonal plumbing for FINDING a message, not part of what was broken, so this calls the consumer
/// directly — exactly what that loop does once it has leased a row (`IPublisher.Publish`).
/// </summary>
public sealed class WorkerWriteFloorTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<CheckoutConfirmedLine> OneLine(Money unitPrice) =>
        [new CheckoutConfirmedLine(
            Product, 1, unitPrice, Money.Of(1_000_000m, unitPrice.Currency), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", Dob)];

    private static IReadOnlyList<OrderLineInput> OneOrderLine(Money unitPrice) =>
        [new OrderLineInput(
            Product, 1, unitPrice, Money.Of(1_000_000m, unitPrice.Currency), 365, "Test Insurer",
            "Somchai", "Jaidee", "1234567890123", Dob)];

    private readonly SqliteConnection _connection;

    public WorkerWriteFloorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    // Bound to MerchantA, mirroring the real dispatcher: each leased message gets its own scope bound to
    // THAT message's merchant (OutboxDispatcher.DispatchBatchAsync's IActorScope.Begin(merchantId)) — the
    // query filter needs a bound actor to read the row back, even though WorkerWriteAuthorizer itself never
    // looks at it.
    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(MerchantA), new ApiHost::Api.BackgroundDispatch.WorkerWriteAuthorizer(), NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task CheckoutConfirmed_insert_and_subsequent_MarkPaid_update_both_survive_the_real_Worker_write_floor()
    {
        var checkoutSessionId = Guid.NewGuid();

        using (var db = NewContext())
        {
            var consumer = new CheckoutConfirmedConsumer(
                new OrderRepository(db), new EfOutbox(db, new SystemClock(), FakeActor.For(MerchantA)),
                new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());

            var notification = new CheckoutConfirmed(
                MerchantA, checkoutSessionId, Money.Of(100m, "THB"), Recipient: null, DateTime.UtcNow,
                OneLine(Money.Of(100m, "THB")));

            // Insert (Order AND its OrderLine) — before task 0/3's fix, WorkerWriteAuthorizer denied these
            // entirely and this threw WriteGuardException.
            await consumer.Handle(notification, CancellationToken.None);
        }

        using (var db = NewContext())
        {
            var order = await db.Set<OrderAggregate>().SingleAsync(o => o.CheckoutSessionId == checkoutSessionId);
            order.MarkPaid(Money.Of(100m, "THB"), DateTime.UtcNow);
            await db.SaveChangesAsync(); // Update — the same gap, surfaced via OrderPaidConsumer's MarkPaid call
        }

        using var verify = NewContext();
        var paid = await verify.Set<OrderAggregate>().SingleAsync(o => o.CheckoutSessionId == checkoutSessionId);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        var line = await verify.Set<OrderLine>().SingleAsync(l => l.OrderId == paid.Id);
        Assert.Equal(Product, line.ProductId);
    }

    [Fact]
    public async Task Deleting_an_order_is_still_denied_by_the_real_Worker_write_floor()
    {
        using var db = NewContext();
        var order = OrderAggregate.Create(MerchantA, Money.Of(1m, "THB"), DateTime.UtcNow, OneOrderLine(Money.Of(1m, "THB")));
        db.Add(order);
        await db.SaveChangesAsync();

        db.Remove(order);
        await Assert.ThrowsAsync<WriteGuardException>(() => db.SaveChangesAsync());
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
