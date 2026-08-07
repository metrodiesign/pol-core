extern alias ApiHost;

using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Domain.Items;
using Persistence.MerchantRuntime;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;
using OrderItem = Orders.Domain.Items.Item;

namespace Hosts.Tests;

/// <summary>
/// Proves Payment consumers may update an existing Order through real background write floor, while Order
/// creation/deletion remain request-owned and denied.
/// </summary>
public sealed class WorkerWriteFloorTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.NewGuid();

    private static IReadOnlyList<OrderItemInput> OneOrderLine(Money unitPrice) =>
        [new OrderItemInput(
            1, unitPrice, "00098-69100/กธ/900001-10", "VMI", "ประกันรถยนต์")];

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

    private MerchantRuntimeDbContext NewSetupContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(MerchantA), AllowAllWriteAuthorizer.Instance, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task MarkPaid_update_survives_real_Worker_write_floor()
    {
        var orderId = Guid.Empty;

        using (var db = NewSetupContext())
        {
            var order = OrderAggregate.Create(
                MerchantA, Money.Of(100m, "THB"), DateTime.UtcNow,
                OneOrderLine(Money.Of(100m, "THB")), orderNo: "ORD6900000001");
            orderId = order.Id;
            db.Add(order);
            await db.SaveChangesAsync();
        }

        using (var db = NewContext())
        {
            var order = await db.Set<OrderAggregate>().SingleAsync(o => o.Id == orderId);
            order.MarkPaid(Guid.NewGuid(), "card", Money.Of(100m, "THB"), DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        using var verify = NewContext();
        var paid = await verify.Set<OrderAggregate>().SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Paid, paid.Status);
        var item = await verify.Set<OrderItem>().SingleAsync(i => i.OrderId == paid.Id);
        Assert.Equal("00098-69100/กธ/900001-10", item.ProductCode);
    }

    [Fact]
    public async Task Deleting_an_order_is_still_denied_by_the_real_Worker_write_floor()
    {
        var orderId = Guid.Empty;
        using (var setup = NewSetupContext())
        {
            var order = OrderAggregate.Create(
                MerchantA, Money.Of(1m, "THB"), DateTime.UtcNow,
                OneOrderLine(Money.Of(1m, "THB")), orderNo: "ORD6900000001");
            orderId = order.Id;
            setup.Add(order);
            await setup.SaveChangesAsync();
        }

        using var db = NewContext();
        var tracked = await db.Set<OrderAggregate>().SingleAsync(o => o.Id == orderId);
        db.Remove(tracked);
        await Assert.ThrowsAsync<WriteGuardException>(() => db.SaveChangesAsync());
    }

    private sealed class FakeActor(bool hasActor, Guid merchantId = default) : IActorContext
    {
        public static FakeActor For(Guid merchantId) => new(true, merchantId);

        public Guid MerchantId => hasActor ? merchantId : throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => hasActor;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    public void Dispose() => _connection.Dispose();
}
