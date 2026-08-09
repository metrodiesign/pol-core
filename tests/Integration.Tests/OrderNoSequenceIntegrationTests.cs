using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders;

namespace Integration.Tests;

/// <summary>
/// purchase-flow-completion REQ-7.1 — the order-number allocator, on the real SQL Server. Three things no
/// unit test can see: the sequence object exists in <c>shop</c>, <c>pol_app</c> actually holds the UPDATE
/// grant it needs to consume values (a missing GRANT is invisible to every SQLite test in the repo and
/// would only surface as a 500 on the first order after deploy), and <c>IX_Orders_OrderNo</c> really
/// refuses a duplicate number. The format itself is asserted against the production formatter.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrderNoSequenceIntegrationTests
{
    [Fact]
    public async Task The_sequence_exists_and_pol_app_can_consume_it()
    {
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        // The bare GRANT check first, so a missing one reads as "no grant" rather than "sequence broken".
        var granted = (int)(await IntegrationDb.ScalarAsync(c,
            """
            SELECT COUNT(*) FROM sys.database_permissions p
            JOIN sys.database_principals dp ON dp.principal_id = p.grantee_principal_id
            JOIN sys.sequences s ON s.object_id = p.major_id
            WHERE dp.name = N'pol_app' AND s.name = N'OrderNoSeq'
              AND p.permission_name = N'UPDATE' AND p.state = 'G';
            """))!;
        Assert.Equal(1, granted);

        // ...then actually consume two values as pol_app: monotonic, and never the same value twice.
        var first = (long)(await IntegrationDb.ScalarAsync(c, "SELECT NEXT VALUE FOR shop.OrderNoSeq;"))!;
        var second = (long)(await IntegrationDb.ScalarAsync(c, "SELECT NEXT VALUE FOR shop.OrderNoSeq;"))!;

        Assert.True(second > first, $"sequence went backwards: {first} -> {second}");
    }

    // The formatter the consumer uses, pinned against a real sequence value: ORD + Buddhist year + 8 digits.
    [Fact]
    public async Task The_minted_number_has_the_documented_shape()
    {
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var value = (long)(await IntegrationDb.ScalarAsync(c, "SELECT NEXT VALUE FOR shop.OrderNoSeq;"))!;

        var orderNo = OrderNoSequence.Format(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), value);

        Assert.Equal(13, orderNo.Length);
        Assert.StartsWith("ORD69", orderNo, StringComparison.Ordinal);   // 2026 + 543 = 2569
        Assert.Equal(value.ToString("D8"), orderNo[5..]);
    }

    // Past 99,999,999 the number no longer fits varchar(13): the formatter must name the exhaustion itself
    // instead of letting the column refuse it as an opaque truncation error (review PR #168).
    [Fact]
    public void A_sequence_value_past_the_8_digit_space_is_refused_by_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => OrderNoSequence.Format(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc), 100_000_000));

        Assert.Contains("8-digit", ex.Message, StringComparison.Ordinal);
    }

    // REQ-7.1 "บังคับ unique" — the index, not just the sequence, is what makes a duplicate impossible.
    [Fact]
    public async Task Two_orders_cannot_share_a_number()
    {
        var orderNo = $"ORD69{Random.Shared.Next(90_000_000, 99_999_999)}";
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        try
        {
            await InsertOrderAsync(c, first, orderNo);

            var ex = await Assert.ThrowsAsync<SqlException>(() => InsertOrderAsync(c, second, orderNo));

            Assert.Contains(ex.Number, new[] { 2601, 2627 });   // the pair MerchantRuntimeUnitOfWork maps to 409
            Assert.Contains("IX_Orders_OrderNo", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.ExecAsync(c, "DELETE shop.Orders WHERE Id IN (@a, @b);", ("@a", first), ("@b", second));
        }
    }

    private static Task InsertOrderAsync(SqlConnection c, Guid orderId, string orderNo) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.Orders
                (Id, MerchantId, OrderNo, AmountAmount, AmountCurrency, Status, CreatedAt,
                 SummaryToken, SummaryTokenExpiresAt, CustomerName, CustomerPhone)
            VALUES (@id, @m, @orderNo, 15000, N'THB', 1, SYSUTCDATETIME(),
                    @token, DATEADD(hour, 72, SYSUTCDATETIME()), N'Probe', '0800000000');
            """,
            ("@id", orderId), ("@m", IntegrationDb.MerchantA), ("@orderNo", orderNo),
            ("@token", Guid.NewGuid().ToString("N")));

    // 2c2p-e2e-fixes AC-1 — the live-sandbox regression: OrderNoSequence used to route
    // `NEXT VALUE FOR` through EF's SqlQueryRaw, which SQL Server refuses once EF wraps it in a
    // derived table (Msg 11719). This calls the real port through the real MerchantRuntimeDbContext
    // (not a raw connection) so a regression here fails the same way it failed in the sandbox.
    [Fact]
    public async Task NextAsync_mints_a_real_order_number_through_EF()
    {
        await using var db = NewContext();
        var sut = new OrderNoSequence(db, new FixedClock());

        var orderNo = await sut.NextAsync(CancellationToken.None);

        Assert.Matches("^ORD69\\d{8}$", orderNo);
    }

    private static MerchantRuntimeDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlServer(IntegrationDb.AppConn).Options,
            new FakeActor(IntegrationDb.MerchantA), AllowAllWriteAuthorizer.Instance, NoOpSecurityTelemetry.Instance);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class FakeActor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}
