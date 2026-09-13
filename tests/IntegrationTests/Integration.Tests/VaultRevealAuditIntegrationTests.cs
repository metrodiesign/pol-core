using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Regression guard for the reveal-audit table's one surviving grant-level invariant post rls-to-query-filter
/// task 8 (RlsTeardownAndOnePrincipal): pol_app is the sole runtime principal now and holds SELECT + INSERT on
/// <c>merch.VaultRevealAudits</c> directly (the migration's own deferred grant — no more <c>usp_vault_audit_head</c>
/// bypass proc, no more BLOCK-on-insert predicate to stop a foreign merchant id, both torn down with RLS). What
/// remains true at the DB level is append-only: no UPDATE/DELETE grant was ever extended to pol_app, so the
/// chain still cannot be trimmed or rewritten in place. Tagged Integration: the default unit run skips these;
/// CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VaultRevealAuditIntegrationTests
{
    private static readonly byte[] Zero32 = new byte[32];

    private static Task InsertAuditAsync(SqlConnection c, Guid merchantId, long seq) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT merch.VaultRevealAudits (MerchantId, SecretName, Seq, PrevHash, Hash, RevealedAt)
            VALUES (@m, N'probe', @seq, @prev, @hash, SYSUTCDATETIME());
            """,
            ("@m", merchantId), ("@seq", seq), ("@prev", Zero32), ("@hash", Zero32));

    [Fact]
    public async Task PolApp_can_insert_its_own_audit_row()
    {
        var merchant = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertAuditAsync(a, merchant, seq: 1); // no throw = INSERT still granted
    }

    [Fact]
    public async Task PolApp_cannot_update_or_delete_the_audit_table()
    {
        var merchant = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertAuditAsync(a, merchant, seq: 1);

        // Append-only invariant (REQ-12.2 equivalent, pol_app side): no UPDATE/DELETE grant on this table.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(a, "UPDATE merch.VaultRevealAudits SET SecretName=N'x' WHERE MerchantId=@m", ("@m", merchant)));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(a, "DELETE merch.VaultRevealAudits WHERE MerchantId=@m", ("@m", merchant)));
    }
}
