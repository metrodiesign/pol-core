using Microsoft.Data.SqlClient;
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
                 SummaryToken, SummaryTokenExpiresAt)
            VALUES (@id, @m, @orderNo, 15000, N'THB', 0, SYSUTCDATETIME(),
                    @token, DATEADD(hour, 72, SYSUTCDATETIME()));
            """,
            ("@id", orderId), ("@m", IntegrationDb.MerchantA), ("@orderNo", orderNo),
            ("@token", Guid.NewGuid().ToString("N")));
}
