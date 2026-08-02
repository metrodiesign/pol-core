using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// The write ORDER that <c>CreateSessionHandler</c>'s lazy expire depends on (purchase-flow-completion
/// REQ-3.2), against the real filtered unique index. Retiring an aged-out session and minting its replacement
/// have to land in one transaction, and the UPDATE that frees <c>IX_PaymentSessions_OrderId_Open</c> has to
/// be sent BEFORE the INSERT that needs it free.
///
/// That ordering cannot be proven offline (SQLite has no filtered indexes) and it cannot be delegated to EF:
/// a single <c>SaveChanges</c> holding both changes orders its commands by
/// <c>ModificationCommandComparer</c>, which sorts by table and key, not by the constraint the pair happens
/// to share. Hence two saves inside one transaction — and hence this test, which pins BOTH directions: the
/// order the handler uses commits, and the order it deliberately avoids is rejected by the index with the
/// very error numbers <c>MerchantRuntimeUnitOfWork.IsUniqueViolation</c> turns into a 409.
///
/// Raw connections, like the rest of this suite (no InternalsVisibleTo into Persistence.MerchantRuntime).
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PaymentSessionExpiryIntegrationTests
{
    private const string IndexName = "IX_PaymentSessions_OrderId_Open";

    // SessionStatus: Created=0, Redirected=1, Paid=2, Failed=3, Expired=4 — 0/1 are the chargeable ones the
    // index filters on. PspExternalChargeId stays NULL so the sibling unique index on (Psp, ExternalChargeId)
    // cannot be the one that fires.
    private static Task InsertSessionAsync(SqlConnection c, SqlTransaction tx, Guid id, Guid orderId, int status) =>
        ExecAsync(c, tx,
            """
            INSERT txn.PaymentSessions
                (Id, MerchantId, OrderId, Method, Psp, Status, CreatedAt, UpdatedAt, AmountAmount, AmountCurrency)
            VALUES (@id, @m, @orderId, N'card', 0, @status, DATEADD(hour, -48, SYSUTCDATETIME()), SYSUTCDATETIME(), 15000, N'THB');
            """,
            ("@id", id), ("@m", IntegrationDb.MerchantA), ("@orderId", orderId), ("@status", status));

    private static Task ExpireAsync(SqlConnection c, SqlTransaction tx, Guid id) =>
        ExecAsync(c, tx,
            "UPDATE txn.PaymentSessions SET Status = 4, UpdatedAt = SYSUTCDATETIME() WHERE Id = @id;",
            ("@id", id));

    private static async Task<int> ExecAsync(SqlConnection c, SqlTransaction tx, string sql, params (string, object)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Expiring_the_stale_session_before_inserting_its_replacement_commits()
    {
        var orderId = Guid.NewGuid();
        var staleId = Guid.NewGuid();
        var freshId = Guid.NewGuid();

        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await using (var seed = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await InsertSessionAsync(c, seed, staleId, orderId, status: 1); // Redirected, 48h old
            await seed.CommitAsync();
        }

        // The handler's shape: phase 1 (UPDATE) and phase 2 (INSERT) as separate statements, one transaction.
        await using (var tx = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await ExpireAsync(c, tx, staleId);
            await InsertSessionAsync(c, tx, freshId, orderId, status: 0);
            await tx.CommitAsync();
        }

        var open = (int)(await IntegrationDb.ScalarAsync(c,
            "SELECT COUNT(*) FROM txn.PaymentSessions WHERE OrderId = @orderId AND Status IN (0, 1);",
            ("@orderId", orderId)))!;
        Assert.Equal(1, open);

        var replacement = (Guid)(await IntegrationDb.ScalarAsync(c,
            "SELECT Id FROM txn.PaymentSessions WHERE OrderId = @orderId AND Status IN (0, 1);",
            ("@orderId", orderId)))!;
        Assert.Equal(freshId, replacement);
    }

    [Fact]
    public async Task Inserting_the_replacement_before_expiring_the_stale_session_is_rejected_by_the_index()
    {
        // The failure mode the two-phase save exists to avoid: with both writes in one batch, nothing
        // guarantees the UPDATE goes first, and this is what the other order costs — the same 2627/2601 the
        // unit of work maps to ConflictException/409, i.e. an order nobody can pay until the TTL passes again.
        var orderId = Guid.NewGuid();
        var staleId = Guid.NewGuid();

        await using var c = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await using (var seed = (SqlTransaction)await c.BeginTransactionAsync())
        {
            await InsertSessionAsync(c, seed, staleId, orderId, status: 1);
            await seed.CommitAsync();
        }

        await using var tx = (SqlTransaction)await c.BeginTransactionAsync();

        var ex = await Assert.ThrowsAsync<SqlException>(() =>
            InsertSessionAsync(c, tx, Guid.NewGuid(), orderId, status: 0));

        Assert.True(ex.Number is 2627 or 2601,
            $"expected SQL 2627/2601 (what MerchantRuntimeUnitOfWork translates to ConflictException/409), got {ex.Number}");
        Assert.Contains(IndexName, ex.Message);

        await tx.RollbackAsync();
    }
}
