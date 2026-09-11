using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the rls-to-query-filter task 6 mechanism against live SQL Server 2025 (REQ-7.2/7.3): the EXACT
/// <c>sp_getapplock</c>(Exclusive, transaction-owned) + head-read + append statements
/// <see cref="Persistence.MerchantRuntime.Vault.VaultAuditAppender"/> issues (that internal port has no
/// InternalsVisibleTo into Integration.Tests — see the port's own doc comment — so this drives the identical
/// SQL directly, the same way <see cref="VaultRevealAuditIntegrationTests"/> already probes the OLD proc
/// mechanism raw). Runs as <c>sa</c> (SESSION_CONTEXT-bound per writer, so the table's BLOCK-insert predicate
/// passes) rather than <c>pol_app</c> — <c>pol_app</c> has no SELECT grant on this table yet (that grant is
/// task 8's "1 principal" collapse); this test proves the LOCKING mechanism is safe under concurrency,
/// independent of which principal eventually gets wired to it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VaultAuditAppenderIntegrationTests
{
    private static readonly byte[] Genesis = new byte[32];

    private static async Task<long> AppendAsync(string connString, Guid merchantId, string secretName, int lockTimeoutMs = 15000)
    {
        await using var connection = await IntegrationDb.OpenAsync(connString, merchantId);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync();

        await using var lockCmd = connection.CreateCommand();
        lockCmd.Transaction = tx;
        lockCmd.CommandText = """
            DECLARE @lock int;
            EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
            SELECT @lock AS Value;
            """;
        lockCmd.Parameters.AddWithValue("@resource", $"vault-audit:{merchantId}");
        lockCmd.Parameters.AddWithValue("@timeout", lockTimeoutMs);
        var lockResult = (int)(await lockCmd.ExecuteScalarAsync())!;
        if (lockResult < 0)
        {
            await tx.RollbackAsync();
            throw new InvalidOperationException("Could not acquire the vault audit chain lock for the merchant.");
        }

        await using var headCmd = connection.CreateCommand();
        headCmd.Transaction = tx;
        headCmd.CommandText = "SELECT TOP 1 Seq, Hash FROM merch.VaultRevealAudits WHERE MerchantId=@m ORDER BY Seq DESC";
        headCmd.Parameters.AddWithValue("@m", merchantId);
        await using var reader = await headCmd.ExecuteReaderAsync();
        long headSeq = 0;
        byte[] headHash = Genesis;
        if (await reader.ReadAsync())
        {
            headSeq = reader.GetInt64(0);
            headHash = (byte[])reader[1];
        }
        await reader.CloseAsync();

        var nextSeq = headSeq + 1;
        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT merch.VaultRevealAudits (MerchantId, SecretName, Seq, PrevHash, Hash, RevealedAt)
            VALUES (@m, @name, @seq, @prev, @hash, SYSUTCDATETIME());
            """;
        insertCmd.Parameters.AddWithValue("@m", merchantId);
        insertCmd.Parameters.AddWithValue("@name", secretName);
        insertCmd.Parameters.AddWithValue("@seq", nextSeq);
        insertCmd.Parameters.AddWithValue("@prev", headHash);
        insertCmd.Parameters.AddWithValue("@hash", Genesis); // hash content not under test here, only Seq/PrevHash chaining
        await insertCmd.ExecuteNonQueryAsync();

        await tx.CommitAsync();
        return nextSeq;
    }

    [Fact]
    public async Task Concurrent_writers_produce_a_contiguous_gap_free_chain_with_no_fork()
    {
        var merchant = Guid.NewGuid();
        const int writers = 10;

        var seqs = await Task.WhenAll(Enumerable.Range(0, writers)
            .Select(i => AppendAsync(IntegrationDb.SaConn, merchant, $"secret-{i}")));

        Assert.Equal(Enumerable.Range(1, writers).Select(i => (long)i), seqs.OrderBy(s => s));

        await using var reader = await IntegrationDb.OpenAsync(IntegrationDb.SaConn, merchant);
        await using var cmd = reader.CreateCommand();
        cmd.CommandText = "SELECT Seq, PrevHash, Hash FROM merch.VaultRevealAudits WHERE MerchantId=@m ORDER BY Seq";
        cmd.Parameters.AddWithValue("@m", merchant);
        await using var rows = await cmd.ExecuteReaderAsync();

        byte[]? previousHash = null;
        var count = 0;
        while (await rows.ReadAsync())
        {
            count++;
            Assert.Equal((long)count, rows.GetInt64(0)); // Seq is contiguous 1..writers, no gaps/duplicates
            var prevHash = (byte[])rows[1];
            Assert.Equal(previousHash ?? Genesis, prevHash); // links to the immediately preceding row -> single chain, no fork
            previousHash = (byte[])rows[2];
        }
        Assert.Equal(writers, count);
    }

    [Fact]
    public async Task A_held_lock_aborts_a_second_writer_that_times_out_waiting_for_it()
    {
        var merchant = Guid.NewGuid();

        await using var holder = await IntegrationDb.OpenAsync(IntegrationDb.SaConn, merchant);
        await using var holderTx = (SqlTransaction)await holder.BeginTransactionAsync();
        await using var holderLockCmd = holder.CreateCommand();
        holderLockCmd.Transaction = holderTx;
        holderLockCmd.CommandText = """
            DECLARE @lock int;
            EXEC @lock = sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
            SELECT @lock AS Value;
            """;
        holderLockCmd.Parameters.AddWithValue("@resource", $"vault-audit:{merchant}");
        var holderLockResult = (int)(await holderLockCmd.ExecuteScalarAsync())!;
        Assert.True(holderLockResult >= 0); // holder acquired the lock and is deliberately not committing yet

        // Second writer uses a short timeout; the holder above is still inside its transaction, so this must abort.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => AppendAsync(IntegrationDb.SaConn, merchant, "should-not-land", lockTimeoutMs: 500));

        await holderTx.RollbackAsync();

        // The lock-fail write never landed — the chain is still empty for this merchant.
        await using var reader = await IntegrationDb.OpenAsync(IntegrationDb.SaConn, merchant);
        var count = (int)(await IntegrationDb.ScalarAsync(reader, "SELECT COUNT(*) FROM merch.VaultRevealAudits WHERE MerchantId=@m", ("@m", merchant)))!;
        Assert.Equal(0, count);
    }
}
