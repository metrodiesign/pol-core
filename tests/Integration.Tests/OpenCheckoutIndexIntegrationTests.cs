using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// The one-live-checkout-per-cart floor (purchase-flow-completion REQ-2.4) against real SQL Server: a cart can
/// hold at most one Started/Confirmed checkout session, while an abandoned one must still let the same cart
/// start a fresh one (REQ-2.5). Neither half can be proven offline — SQLite has no filtered indexes — so the
/// model-shape half lives in <c>Architecture.Tests/OpenCheckoutIndexTests</c> and the enforcement half is here.
///
/// Raw connections, like the rest of this suite, and the same pinning as
/// <see cref="OpenSessionIndexIntegrationTests"/>: the last hop (SQL 2627/2601 -> ConflictException -> 409)
/// is asserted by the exact error NUMBERS <c>MerchantRuntimeUnitOfWork.IsUniqueViolation</c> keys on, plus the
/// index name that raised them, so another unique index on this table could not be mistaken for a pass.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenCheckoutIndexIntegrationTests
{
    private const string IndexName = "IX_CheckoutSessions_CartId_Open";

    // Checkouts.Domain.SessionStatus: Started=0, Confirmed=1, Abandoned=2 — 0/1 are the live ones the index
    // filters on.
    private static Task InsertSessionAsync(SqlConnection c, Guid cartId, int status) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT shop.CheckoutSessions
                (Id, MerchantId, CartId, Status, CreatedAt, AmountAmount, AmountCurrency)
            VALUES (@id, @m, @cartId, @status, SYSUTCDATETIME(), 15000, N'THB');
            """,
            ("@id", Guid.NewGuid()), ("@m", IntegrationDb.MerchantA), ("@cartId", cartId), ("@status", status));

    [Fact]
    public async Task The_index_is_unique_filtered_and_keyed_on_CartId_alone()
    {
        // sa, not pol_app: SQL Server masks sys.indexes.filter_definition from a principal holding only
        // SELECT/INSERT/UPDATE (it reads back NULL) — same reason OpenSessionIndexIntegrationTests uses sa
        // for its DDL-identity assertion.
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);

        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT i.is_unique, i.has_filter, i.filter_definition, COUNT(*) AS KeyColumns,
                   MIN(col.name) AS KeyColumn
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = i.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('shop.CheckoutSessions') AND i.name = @name
            GROUP BY i.is_unique, i.has_filter, i.filter_definition;
            """;
        cmd.Parameters.AddWithValue("@name", IndexName);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"{IndexName} does not exist on shop.CheckoutSessions.");
        Assert.True(reader.GetBoolean(0), "index is not unique");
        Assert.True(reader.GetBoolean(1), "index is not filtered");
        // SQL Server normalises the configured predicate "[Status] IN (0, 1)" when it stores it.
        Assert.Equal("([Status] IN ((0), (1)))", reader.GetString(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal("CartId", reader.GetString(4));
    }

    [Fact]
    public async Task A_second_live_checkout_for_the_same_cart_is_rejected()
    {
        var cartId = Guid.NewGuid();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(c, cartId, status: 0); // Started

        // The request that got past StartCheckoutHandler's GetOpenForCartAsync pre-check. Confirmed is the
        // other live status, so the index must reject it too, not just an exact status repeat.
        var ex = await Assert.ThrowsAsync<SqlException>(() => InsertSessionAsync(c, cartId, status: 1));

        Assert.True(ex.Number is 2627 or 2601,
            $"expected SQL 2627/2601 (what MerchantRuntimeUnitOfWork translates to ConflictException/409), got {ex.Number}");
        Assert.Contains(IndexName, ex.Message);
    }

    [Fact]
    public async Task An_abandoned_checkout_does_not_block_a_fresh_one_on_the_same_cart()
    {
        var cartId = Guid.NewGuid();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        // Abandoned sits outside the filter on purpose — abandoning is exactly what reopens the cart, so any
        // number of abandoned attempts may coexist (REQ-2.5).
        await InsertSessionAsync(c, cartId, status: 2);
        await InsertSessionAsync(c, cartId, status: 2);

        await InsertSessionAsync(c, cartId, status: 0); // Started — the retry the index must allow

        var live = (int)(await IntegrationDb.ScalarAsync(c,
            "SELECT COUNT(*) FROM shop.CheckoutSessions WHERE CartId = @cartId AND Status IN (0, 1);",
            ("@cartId", cartId)))!;
        Assert.Equal(1, live);
    }
}
