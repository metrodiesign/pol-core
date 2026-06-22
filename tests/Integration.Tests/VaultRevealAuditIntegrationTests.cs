using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Regression guards for the PR4b reveal-audit RLS surface against live SQL Server 2025: pol_app can only
/// INSERT (never SELECT/UPDATE/DELETE) the audit table, cannot stamp another tenant's id (BLOCK predicate),
/// and reaches the chain head only through the bypass proc usp_vault_audit_head. These cannot run under the
/// SQLite unit harness (no RLS/grants/proc), so they are tagged Integration and run against the SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VaultRevealAuditIntegrationTests
{
    private static readonly byte[] Zero32 = new byte[32];

    private static Task InsertAuditAsync(SqlConnection c, Guid tenantId, long seq, SqlTransaction? tx = null)
    {
        const string sql = """
            INSERT producer.VaultRevealAudits (TenantId, SecretName, Seq, PrevHash, Hash, RevealedAtUtc)
            VALUES (@t, N'probe', @seq, @prev, @hash, SYSUTCDATETIME());
            """;
        return Exec(c, sql, tx, ("@t", tenantId), ("@seq", seq), ("@prev", Zero32), ("@hash", Zero32));
    }

    private static async Task Exec(SqlConnection c, string sql, SqlTransaction? tx, params (string, object)[] args)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        if (tx is not null) cmd.Transaction = tx;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task PolApp_can_insert_its_own_audit_row()
    {
        var tenant = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, tenant);
        await InsertAuditAsync(a, tenant, seq: 1); // no throw = INSERT granted + BLOCK satisfied
    }

    [Fact]
    public async Task PolApp_cannot_select_the_audit_table()
    {
        var tenant = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, tenant);

        // No SELECT grant -> permission error. This is the core tamper-resistance: a tenant can append but
        // can never read (hence never trim/rewrite) its own chain.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(a, "SELECT COUNT(*) FROM producer.VaultRevealAudits"));
    }

    [Fact]
    public async Task PolApp_cannot_update_or_delete_the_audit_table()
    {
        var tenant = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, tenant);
        await InsertAuditAsync(a, tenant, seq: 1);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(a, "UPDATE producer.VaultRevealAudits SET SecretName=N'x' WHERE TenantId=@t", ("@t", tenant)));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(a, "DELETE producer.VaultRevealAudits WHERE TenantId=@t", ("@t", tenant)));
    }

    [Fact]
    public async Task Stamping_a_foreign_tenant_id_onto_an_audit_row_is_blocked()
    {
        var tenant = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, tenant);

        await Assert.ThrowsAsync<SqlException>(() => InsertAuditAsync(a, Guid.NewGuid(), seq: 1));
    }

    [Fact]
    public async Task Bypass_proc_returns_the_chain_head_for_the_tenant()
    {
        var tenant = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, tenant);
        await InsertAuditAsync(a, tenant, seq: 1);
        await InsertAuditAsync(a, tenant, seq: 2);

        // The proc's sp_getapplock is transaction-owned, so it must be called inside a transaction.
        await using var tx = (SqlTransaction)await a.BeginTransactionAsync();
        await using var cmd = a.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "EXEC producer.usp_vault_audit_head @TenantId = @t";
        cmd.Parameters.AddWithValue("@t", tenant);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(reader.GetOrdinal("LastSeq")));
        await reader.CloseAsync();
        await tx.CommitAsync();
    }
}
