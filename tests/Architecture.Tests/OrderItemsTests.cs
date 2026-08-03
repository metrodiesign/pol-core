using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Domain.Items;
using Persistence.MerchantRuntime;
using SharedKernel;
using OrderItem = Orders.Domain.Items.Item;

namespace Architecture.Tests;

/// <summary>
/// EF round-trip for <see cref="Order.Items"/> (insurance-pivot REQ-6) over
/// <see cref="MerchantRuntimeDbContext"/> backed by in-memory SQLite (mirrors
/// <see cref="ProductRepositoryListTests"/>'s constructor pattern). Proves the composite alternate-key/FK
/// mapping round-trips every line field, and that deleting the parent <see cref="Order"/> cascades to its
/// <see cref="OrderItem"/> rows.
/// </summary>
public sealed class OrderItemsTests : IDisposable
{
    // Every persisted order needs its own number now (IX_Orders_OrderNo is UNIQUE) — a fixed literal in a
    // helper called more than once per database would collide.
    private static int _orderNoCounter;
    private static string NextOrderNo() => $"ORD69{Interlocked.Increment(ref _orderNoCounter):D8}";

    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public OrderItemsTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.For(MerchantA), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private static Order NewOrderWithOneLine() =>
        Order.Create(MerchantA, Money.Of(15000m, "THB"), At,
            [new OrderItemInput(
                ProductA, 1, Money.Of(15000m, "THB"), "00098-69100/กธ/900001-10", "VMI", "POLICY",
                "P-900001", At.Date, At.Date.AddYears(1),
                "Somchai", "Jaidee", "1234567890123", Dob)], orderNo: NextOrderNo());

    [Fact]
    public async Task Order_with_a_line_round_trips_through_EF()
    {
        var order = NewOrderWithOneLine();
        using (var writer = NewContext())
        {
            writer.Add(order);
            await writer.SaveChangesAsync();
        }

        using var reader = NewContext();
        var reloaded = await reader.Orders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);

        var item = Assert.Single(reloaded.Items);
        Assert.Equal(ProductA, item.ProductId);
        Assert.Equal(order.Id, item.OrderId);
        Assert.Equal(MerchantA, item.MerchantId);
        Assert.Equal(Money.Of(15000m, "THB"), item.UnitPrice);
        Assert.Equal("00098-69100/กธ/900001-10", item.DocumentNo);
        Assert.Equal("VMI", item.ProductGroup);
        Assert.Equal("POLICY", item.DocumentType);
        Assert.Equal("P-900001", item.PolicyNumber);
        Assert.Equal(At.Date, item.StartDate);
        Assert.Equal(At.Date.AddYears(1), item.EndDate);
        Assert.Equal("Somchai", item.InsuredFirstName);
        Assert.Equal("Jaidee", item.InsuredLastName);
        Assert.Equal("1234567890123", item.InsuredIdNumber);
        Assert.Equal(Dob, item.InsuredDateOfBirth);
    }

    [Fact]
    public async Task Deleting_the_order_cascades_to_its_lines()
    {
        var order = NewOrderWithOneLine();
        using (var writer = NewContext())
        {
            writer.Add(order);
            await writer.SaveChangesAsync();
        }

        using (var deleter = NewContext())
        {
            // Loaded WITH its items so EF's client-side cascade marks them Deleted too, regardless of
            // whether the SQLite provider's FK pragma is enabled — the same guarantee a real SQL Server
            // ON DELETE CASCADE gives when the dependents aren't loaded.
            var toDelete = await deleter.Orders.Include(o => o.Items).SingleAsync(o => o.Id == order.Id);
            deleter.Orders.Remove(toDelete);
            await deleter.SaveChangesAsync();
        }

        using var reader = NewContext();
        Assert.Empty(await reader.Set<OrderItem>().Where(i => i.OrderId == order.Id).ToListAsync());
    }

    public void Dispose() => _connection.Dispose();
}
