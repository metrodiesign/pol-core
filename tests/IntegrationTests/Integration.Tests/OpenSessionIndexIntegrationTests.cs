using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// The one-open-session-per-order floor (captive-payment-alignment REQ-2.4/2.5) against real SQL Server: one
/// order can hold at most one CHARGEABLE payment session, while a terminal attempt must still let the same
/// order start a fresh one (REQ-7.4). Neither half can be proven offline — SQLite has no filtered indexes, and
/// <c>Session.RowVersion</c> is mapped <c>IsRowVersion()</c>, which SQLite cannot generate at all (EF omits the
/// column and the insert dies on NOT NULL), so no session row can even be created there. The model-shape half
/// lives in <c>Architecture.Tests/OpenSessionIndexTests</c>; the enforcement half is here.
///
/// Raw connections, like the rest of this suite: Integration.Tests deliberately has no InternalsVisibleTo into
/// Persistence.MerchantRuntime (that assembly grants it per-consumer, by design), so the last hop —
/// SQL 2627/2601 -> ConflictException -> 409 — is pinned by asserting the exact error NUMBERS
/// <c>MerchantRuntimeUnitOfWork.IsUniqueViolation</c> keys on, plus the name of the index that raised them, so
/// a violation of the OTHER unique index on this table could not be mistaken for a pass.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenSessionIndexIntegrationTests
{
    private const string IndexName = "IX_PaymentSessions_OrderId_Open";

    // SessionStatus: Created=1, Redirected=2, Paid=3, Failed=4, Expired=5 — 1/2 are the chargeable ones the
    // index filters on. Psp=1 (2C2P). PspExternalChargeId stays NULL so the sibling
    // unique index on (Psp, PspExternalChargeId) cannot be the one that fires.
    private static Task InsertSessionAsync(SqlConnection c, Guid orderId, int status) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT txn.PaymentSessions
                (Id, MerchantId, OrderId, Method, Psp, Status, CreatedAt, UpdatedAt, AmountAmount, AmountCurrency)
            VALUES (@id, @m, @orderId, N'card', 1, @status, SYSUTCDATETIME(), SYSUTCDATETIME(), 15000, N'THB');
            """,
            ("@id", Guid.NewGuid()), ("@m", IntegrationDb.MerchantA), ("@orderId", orderId), ("@status", status));

    [Fact]
    public async Task The_index_is_unique_filtered_and_keyed_on_OrderId_alone()
    {
        // sa, not pol_app: SQL Server's metadata-visibility rules mask sys.indexes.filter_definition (a
        // definition column) from a principal holding only SELECT/INSERT/UPDATE — it reads back NULL while
        // is_unique/has_filter come through fine. This assertion is about the DDL that got applied, not about
        // what the runtime principal may see, so it uses the DDL identity (same reason the vault-audit applock
        // tests do).
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);

        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            """
            SELECT i.is_unique, i.has_filter, i.filter_definition, COUNT(*) AS KeyColumns,
                   MIN(col.name) AS KeyColumn
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns col ON col.object_id = i.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID('txn.PaymentSessions') AND i.name = @name
            GROUP BY i.is_unique, i.has_filter, i.filter_definition;
            """;
        cmd.Parameters.AddWithValue("@name", IndexName);
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), $"{IndexName} does not exist on txn.PaymentSessions.");
        Assert.True(reader.GetBoolean(0), "index is not unique");
        Assert.True(reader.GetBoolean(1), "index is not filtered");
        // SQL Server normalises the configured predicate "[Status] IN (1, 2)" when it stores it.
        Assert.Equal("([Status] IN ((1), (2)))", reader.GetString(2));
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal("OrderId", reader.GetString(4));
    }

    [Fact]
    public async Task A_second_chargeable_session_for_the_same_order_is_rejected()
    {
        var orderId = Guid.NewGuid();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(c, orderId, status: 1); // Created

        // A concurrent request that got past the handler's open-session pre-check: Redirected is the other
        // chargeable status, so the index must reject it too, not just an exact status repeat.
        var ex = await Assert.ThrowsAsync<SqlException>(() => InsertSessionAsync(c, orderId, status: 2));

        Assert.True(ex.Number is 2627 or 2601,
            $"expected SQL 2627/2601 (what MerchantRuntimeUnitOfWork translates to ConflictException/409), got {ex.Number}");
        Assert.Contains(IndexName, ex.Message);
    }

    [Fact]
    public async Task A_terminal_session_does_not_block_a_fresh_attempt_on_the_same_order()
    {
        var orderId = Guid.NewGuid();
        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        // Paid/Failed/Expired all sit outside the filter, so they may coexist without limit — a declined
        // attempt must never kill the order (REQ-7.4).
        await InsertSessionAsync(c, orderId, status: 4); // Failed
        await InsertSessionAsync(c, orderId, status: 5); // Expired
        await InsertSessionAsync(c, orderId, status: 3); // Paid

        await InsertSessionAsync(c, orderId, status: 1); // Created — the retry the index must allow

        var open = (int)(await IntegrationDb.ScalarAsync(c,
            "SELECT COUNT(*) FROM txn.PaymentSessions WHERE OrderId = @orderId AND Status IN (1, 2);",
            ("@orderId", orderId)))!;
        Assert.Equal(1, open);
    }
}
