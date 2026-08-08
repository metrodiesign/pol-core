using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Domain.Items;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using SharedKernel;

namespace Architecture.Tests;

/// <summary>
/// The two reads the payment path gained in captive-payment-alignment task 2, exercised against a REAL
/// provider (in-memory SQLite, same harness as <see cref="OrderItemsTests"/>) rather than a fake — both are
/// LINQ that can only fail at translation time, and a fake repository would happily "pass" either way.
/// <c>PayableOrderReader</c> projects the Money complex type's scalars and re-composes them in memory; this
/// also pins the merchant floor for the payment path (another company's order reads back as null, REQ-1.2,
/// so create-session reports it missing rather than forbidden).
///
/// Scope limit, deliberate: sessions cannot be INSERTed through this harness at all —
/// <c>Session.RowVersion</c> is mapped <c>IsRowVersion()</c>, which SQLite cannot generate, so EF omits the
/// column and the NOT NULL constraint fires. The open-session query is therefore proven here only to
/// translate and execute; that Created/Redirected count as open while Failed/Expired do not is asserted at
/// the handler level (<c>CreateSessionHandlerTests</c>) and against real SQL Server by task 4's integration
/// test.
/// </summary>
public sealed class PaymentPricingQueryTests : IDisposable
{
    // Every persisted order needs its own number now (IX_Orders_OrderNo is UNIQUE) — a fixed literal in a
    // helper called more than once per database would collide.
    private static int _orderNoCounter;
    private static string NextOrderNo() => $"ORD69{Interlocked.Increment(ref _orderNoCounter):D8}";

    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Money Amount = Money.Of(15000m, "THB");
    private static readonly DateTime At = new(2026, 7, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public PaymentPricingQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext(MerchantA);
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewContext(Guid merchantId) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActorContext.For(merchantId), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private async Task<Order> SeedOrderAsync(Guid merchantId, bool paid = false)
    {
        var order = Order.Create(merchantId, Amount, At,
            [new OrderItemInput(
                1, Amount, "00098-69100/กธ/900001-10", "VMI", "ประกันรถยนต์")], orderNo: NextOrderNo());
        if (paid)
            order.MarkPaid(Guid.NewGuid(), "card", Amount, At);

        using var writer = NewContext(merchantId);
        writer.Add(order);
        await writer.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task The_payable_order_read_returns_the_orders_own_amount_and_currency()
    {
        var order = await SeedOrderAsync(MerchantA);

        using var db = NewContext(MerchantA);
        var payable = await new PayableOrderReader(db).GetAsync(order.Id, default);

        Assert.NotNull(payable);
        Assert.Equal(order.Id, payable!.OrderId);
        Assert.Equal(Amount, payable.Amount);
        Assert.Equal(15000m, payable.Amount.Amount);
        Assert.Equal("THB", payable.Amount.Currency);
        Assert.True(payable.CanOpenPaymentAttempt);
    }

    [Fact]
    public async Task A_paid_order_reads_back_as_no_longer_awaiting_payment()
    {
        var order = await SeedOrderAsync(MerchantA, paid: true);

        using var db = NewContext(MerchantA);
        var payable = await new PayableOrderReader(db).GetAsync(order.Id, default);

        Assert.NotNull(payable);
        Assert.False(payable!.CanOpenPaymentAttempt);
    }

    [Fact]
    public async Task Another_companys_order_is_invisible_to_the_payment_path()
    {
        var foreignOrder = await SeedOrderAsync(MerchantB);

        using var db = NewContext(MerchantA);
        var payable = await new PayableOrderReader(db).GetAsync(foreignOrder.Id, default);

        Assert.Null(payable); // indistinguishable from a missing order -> 404, never 403
    }

    [Fact]
    public async Task A_missing_order_reads_back_as_null()
    {
        using var db = NewContext(MerchantA);

        Assert.Null(await new PayableOrderReader(db).GetAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task The_open_session_lookup_translates_and_reports_none_for_an_order_with_no_sessions()
    {
        using var db = NewContext(MerchantA);

        Assert.Null(await new SessionRepository(db).GetOpenForOrderAsync(Guid.NewGuid(), default));
    }

    public void Dispose() => _connection.Dispose();
}
