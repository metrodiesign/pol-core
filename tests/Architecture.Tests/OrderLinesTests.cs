using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Domain.Lines;
using Persistence.MerchantRuntime;
using SharedKernel;
using OrderLine = Orders.Domain.Lines.Line;

namespace Architecture.Tests;

/// <summary>
/// EF round-trip for <see cref="Order.Lines"/> (insurance-pivot REQ-6) over
/// <see cref="MerchantRuntimeDbContext"/> backed by in-memory SQLite (mirrors
/// <see cref="ProductRepositoryListTests"/>'s constructor pattern). Proves the composite alternate-key/FK
/// mapping round-trips every line field, and that deleting the parent <see cref="Order"/> cascades to its
/// <see cref="OrderLine"/> rows.
/// </summary>
public sealed class OrderLinesTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public OrderLinesTests()
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
            [new OrderLineInput(
                ProductA, 1, Money.Of(15000m, "THB"), Money.Of(1_000_000m, "THB"), 365, "Test Insurer",
                "Somchai", "Jaidee", "1234567890123", Dob)]);

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
        var reloaded = await reader.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);

        var line = Assert.Single(reloaded.Lines);
        Assert.Equal(ProductA, line.ProductId);
        Assert.Equal(order.Id, line.OrderId);
        Assert.Equal(MerchantA, line.MerchantId);
        Assert.Equal(Money.Of(15000m, "THB"), line.UnitPrice);
        Assert.Equal(Money.Of(1_000_000m, "THB"), line.SumInsured);
        Assert.Equal(365, line.CoverageDurationDays);
        Assert.Equal("Test Insurer", line.Insurer);
        Assert.Equal("Somchai", line.InsuredFirstName);
        Assert.Equal("Jaidee", line.InsuredLastName);
        Assert.Equal("1234567890123", line.InsuredIdNumber);
        Assert.Equal(Dob, line.InsuredDateOfBirth);
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
            // Loaded WITH its lines so EF's client-side cascade marks them Deleted too, regardless of
            // whether the SQLite provider's FK pragma is enabled — the same guarantee a real SQL Server
            // ON DELETE CASCADE gives when the dependents aren't loaded.
            var toDelete = await deleter.Orders.Include(o => o.Lines).SingleAsync(o => o.Id == order.Id);
            deleter.Orders.Remove(toDelete);
            await deleter.SaveChangesAsync();
        }

        using var reader = NewContext();
        Assert.Empty(await reader.Set<OrderLine>().Where(l => l.OrderId == order.Id).ToListAsync());
    }

    public void Dispose() => _connection.Dispose();
}
