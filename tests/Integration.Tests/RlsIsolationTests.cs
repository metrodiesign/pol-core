using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the multi-merchant isolation floor (PLAN #3 / rf1-schema-reset T5) against live SQL Server 2025
/// with the real principals and security policy. These are the regression guards for the RLS migration: if
/// a future change weakens a predicate, a grant, or a resolve proc, one of these fails. Tagged Integration so
/// the default unit run (Category!=Integration) skips them; CI runs them against a SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RlsIsolationTests
{
    private static int AsInt(object? o) => (int)o!;

    [Fact]
    public async Task MerchantA_row_is_invisible_to_MerchantB()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.MerchantA);

        await using var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantB);
        var seen = AsInt(await IntegrationDb.ScalarAsync(b,
            "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task MerchantA_can_see_its_own_row()
    {
        var id = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);
        await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.MerchantA);

        var seen = AsInt(await IntegrationDb.ScalarAsync(a,
            "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Writing_a_foreign_merchant_id_is_blocked()
    {
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);

        // Block predicate (AFTER INSERT): a merchant cannot stamp another merchant's id onto its rows.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertProductAsync(a, Guid.NewGuid(), IntegrationDb.MerchantB));
    }

    // T5: pol_admin was REMOVED from pol_rls_bypass — the keyed admin PolDbContext now stamps its own
    // SESSION_CONTEXT (AdminActorContext) and goes through the SAME predicate as every other principal. A
    // bare pol_admin connection with no SESSION_CONTEXT at all (no MerchantId, no UserId) no longer sees
    // anything — the opposite of the pre-rf1 blanket bypass. This is the core regression guard for T5.
    [Fact]
    public async Task Admin_without_a_bound_platform_user_sees_nothing()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.MerchantA);

        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var seen = AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(0, seen);
    }

    // T5's replacement for the blanket bypass: a pol_admin connection bound to a Super platform user (the
    // sentinel empty-MerchantId + a resolvable UserId with Tier=Super) sees every merchant's rows via
    // sec.fn_merchant_predicate's platform branch — cross-merchant provisioning/read-back still works, just
    // through an authenticated identity instead of an unconditional role bypass.
    [Fact]
    public async Task Admin_bound_as_super_sees_every_merchant()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.MerchantA);

        await using var admin = await IntegrationDb.OpenAsNewSuperUserAsync();
        var seen = AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Sysadmin_without_context_or_bypass_sees_nothing()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.MerchantA);

        // Proven on live SQL: RLS applies even to sysadmin/dbo. The predicate is the sole authority.
        await using var sa = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        var seen = AsInt(await IntegrationDb.ScalarAsync(sa,
            "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task App_cannot_read_outbox_payloads()
    {
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);

        // pol_app has INSERT but NO SELECT on the outbox -> it can never read another merchant's payload.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(a, "SELECT COUNT(*) FROM txn.OutboxMessages"));
    }

    [Fact]
    public async Task App_cannot_forge_a_foreign_merchant_outbox_row()
    {
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);

        const string insert =
            "INSERT txn.OutboxMessages (Id,MerchantId,Type,Payload,OccurredAt,Attempts) " +
            "VALUES (@id,@m,N'T',N'{}',SYSUTCDATETIME(),0)";

        // Own merchant: allowed.
        await IntegrationDb.ExecAsync(a, insert, ("@id", Guid.NewGuid()), ("@m", IntegrationDb.MerchantA));

        // Foreign merchant: blocked by the BLOCK-after-insert predicate.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(a, insert, ("@id", Guid.NewGuid()), ("@m", IntegrationDb.MerchantB)));
    }

    [Fact]
    public async Task Webhook_resolve_proc_returns_merchant_while_direct_read_stays_blocked()
    {
        // Fresh merchant per run: PspConnections has a UNIQUE (MerchantId, Psp) index, so reusing a fixed
        // merchant would collide on re-runs against a persistent dev database. T5/design.md: PspConnections
        // is now admin-provisioned only (pol_app is SELECT-only), and it carries the merchant BLOCK-on-insert
        // predicate too, so the insert needs a bound Super platform user, not a bare pol_admin connection.
        var merchant = Guid.NewGuid();
        var connId = Guid.NewGuid();
        await using (var admin = await IntegrationDb.OpenAsNewSuperUserAsync())
            await IntegrationDb.InsertPspConnectionAsync(admin, connId, merchant);

        // pol_app with NO bound merchant: it cannot read the connection directly...
        await using var noCtx = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var direct = AsInt(await IntegrationDb.ScalarAsync(noCtx,
            "SELECT COUNT(*) FROM txn.PspConnections WHERE Id=@id", ("@id", connId)));
        Assert.Equal(0, direct);

        // ...but the EXECUTE-AS-bypass proc resolves its merchant id.
        var resolved = (Guid)(await IntegrationDb.ScalarAsync(noCtx,
            "EXEC sec.usp_resolve_webhook_merchant @PspConnectionId=@id", ("@id", connId)))!;
        Assert.Equal(merchant, resolved);
    }

    [Fact]
    public async Task Host_principals_have_the_expected_bypass_membership()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        Assert.Equal("pol_app", (string)(await IntegrationDb.ScalarAsync(app, "SELECT SUSER_SNAME()"))!);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(app, "SELECT IS_ROLEMEMBER('pol_rls_bypass')")));

        // T5: pol_admin is deliberately NOT a bypass member anymore (see Admin_without_a_bound_platform_user_sees_nothing).
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(admin, "SELECT IS_ROLEMEMBER('pol_rls_bypass')")));

        await using var worker = await IntegrationDb.OpenAsync(IntegrationDb.WorkerConn);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(worker, "SELECT IS_ROLEMEMBER('pol_rls_bypass')")));
    }

    // --- T5/REQ-3 exhaustive matrix (rf1-schema-reset task 4): every table under the policy, both Super and
    // Scoped platform branches, the REQ-3.11 fail-closed guard, cross-scope write BLOCK, the sentinel-only guard,
    // the write-side of "no context", schema ownership, and the remaining 2 resolver procs. The tests above cover
    // shop.Products + txn.OutboxMessages + the webhook proc; these fill in the rest of the matrix so no object
    // under sec.MerchantIsolationPolicy is unproven. ---

    public static IEnumerable<object[]> MerchantScopedProbes()
    {
        yield return new object[] { "shop.Carts",
            (Func<SqlConnection, Guid, Guid, Task>)((c, id, m) => IntegrationDb.InsertCartAsync(c, id, m)) };
        yield return new object[] { "shop.CheckoutSessions",
            (Func<SqlConnection, Guid, Guid, Task>)((c, id, m) =>
                IntegrationDb.InsertCheckoutSessionAsync(c, id, m, Guid.NewGuid(), status: 0, amount: 100m, currency: "THB")) };
        yield return new object[] { "shop.Orders",
            (Func<SqlConnection, Guid, Guid, Task>)((c, id, m) =>
                IntegrationDb.InsertOrderAsync(c, id, m, status: 1, amount: 100m, currency: "THB")) };
        yield return new object[] { "txn.PaymentSessions",
            (Func<SqlConnection, Guid, Guid, Task>)((c, id, m) =>
                IntegrationDb.InsertPaymentSessionAsync(c, id, m, Guid.NewGuid(), 100m, "THB", "card", psp: 0, status: 0)) };
        // Parent-scoped (no MerchantId column of its own — sec.fn_cartitem_predicate joins through shop.Carts),
        // but the visibility check is still the same shape: a fresh cart is created inline per probe.
        yield return new object[] { "shop.CartItems",
            (Func<SqlConnection, Guid, Guid, Task>)(async (c, id, m) =>
            {
                var cartId = Guid.NewGuid();
                await IntegrationDb.InsertCartAsync(c, cartId, m);
                await IntegrationDb.InsertCartItemAsync(c, id, cartId, Guid.NewGuid(), 1, 100m, "THB");
            }) };
    }

    [Theory]
    [MemberData(nameof(MerchantScopedProbes))]
    public async Task Merchant_isolation_holds_across_the_policy_matrix(string table, Func<SqlConnection, Guid, Guid, Task> insert)
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
        {
            await insert(a, id, IntegrationDb.MerchantA);
            var own = AsInt(await IntegrationDb.ScalarAsync(a, $"SELECT COUNT(*) FROM {table} WHERE Id=@id", ("@id", id)));
            Assert.Equal(1, own);
        }

        await using var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantB);
        var foreign = AsInt(await IntegrationDb.ScalarAsync(b, $"SELECT COUNT(*) FROM {table} WHERE Id=@id", ("@id", id)));
        Assert.Equal(0, foreign);
    }

    [Fact]
    public async Task PspConnection_is_invisible_across_merchants()
    {
        // Fresh merchants per run (not the shared MerchantA/MerchantB constants): PspConnections has a UNIQUE
        // (MerchantId, Psp) index, so reusing a fixed merchant would collide on re-runs against a persistent DB
        // (same trap Webhook_resolve_proc_returns_merchant_while_direct_read_stays_blocked already avoids above).
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();
        var id = Guid.NewGuid();
        await using (var admin = await IntegrationDb.OpenAsNewSuperUserAsync())
            await IntegrationDb.InsertPspConnectionAsync(admin, id, merchantA);

        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantA);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(a, "SELECT COUNT(*) FROM txn.PspConnections WHERE Id=@id", ("@id", id))));

        await using var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantB);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(b, "SELECT COUNT(*) FROM txn.PspConnections WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task IdempotencyRecord_is_invisible_across_merchants()
    {
        var key = "probe-" + Guid.NewGuid().ToString("N");
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
            await IntegrationDb.InsertIdempotencyRecordAsync(a, key, IntegrationDb.MerchantA, "test-context");

        await using var own = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(own, "SELECT COUNT(*) FROM txn.IdempotencyRecords WHERE [Key]=@k", ("@k", key))));

        await using var foreign = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantB);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(foreign, "SELECT COUNT(*) FROM txn.IdempotencyRecords WHERE [Key]=@k", ("@k", key))));
    }

    [Fact]
    public async Task VaultSecret_is_invisible_across_merchants()
    {
        var name = "probe-" + Guid.NewGuid().ToString("N")[..12];
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
            await IntegrationDb.InsertVaultSecretAsync(a, IntegrationDb.MerchantA, name, "key-1", "hint");

        await using var own = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(own,
            "SELECT COUNT(*) FROM merch.VaultSecrets WHERE MerchantId=@m AND Name=@name", ("@m", IntegrationDb.MerchantA), ("@name", name))));

        await using var foreign = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantB);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(foreign,
            "SELECT COUNT(*) FROM merch.VaultSecrets WHERE MerchantId=@m AND Name=@name", ("@m", IntegrationDb.MerchantA), ("@name", name))));
    }

    [Fact]
    public async Task App_cannot_forge_a_foreign_merchant_vault_reveal_audit()
    {
        // Fresh merchants per run: VaultRevealAudits has a UNIQUE (MerchantId, Seq) index and this probe always
        // writes Seq=1, so reusing a fixed merchant would collide on re-runs against a persistent DB.
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantA);

        // Own merchant: allowed (this is also the "app cannot read it back" table — pol_app has INSERT only).
        await IntegrationDb.InsertVaultRevealAuditAsync(a, merchantA, "probe-secret", DateTime.UtcNow);

        // Foreign merchant: blocked by the BLOCK-after-insert predicate.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertVaultRevealAuditAsync(a, merchantB, "probe-secret", DateTime.UtcNow));
    }

    [Fact]
    public async Task Merchant_self_row_is_visible_only_to_itself()
    {
        // merch.Merchants is self-row scoped: the predicate is on Id, not a separate MerchantId column, so the
        // session's MerchantId must equal the row's own identity to see it.
        var id = Guid.NewGuid();
        var code = "probe-" + id.ToString("N")[..12];
        await using (var super = await IntegrationDb.OpenAsNewSuperUserAsync())
            await IntegrationDb.InsertMerchantAsync(super, id, code);

        await using var self = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, id);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(self, "SELECT COUNT(*) FROM merch.Merchants WHERE Id=@id", ("@id", id))));

        await using var other = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantB);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(other, "SELECT COUNT(*) FROM merch.Merchants WHERE Id=@id", ("@id", id))));
    }

    // REQ-3.7: merch.Merchants joined the policy in rf1 (Tenants never was) specifically so provisioning a brand
    // new merchant is Super-only at the DB — a new Id can never already be in admin.MerchantAccess, so a
    // Scoped platform user's insert is BLOCKed regardless of which merchants it is assigned to.
    [Fact]
    public async Task Super_can_insert_a_new_merchant()
    {
        var id = Guid.NewGuid();
        var code = "probe-" + id.ToString("N")[..12];
        await using var super = await IntegrationDb.OpenAsNewSuperUserAsync();

        await IntegrationDb.InsertMerchantAsync(super, id, code);

        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(super, "SELECT COUNT(*) FROM merch.Merchants WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task Scoped_admin_cannot_insert_a_new_merchant()
    {
        await using var scoped = await IntegrationDb.OpenAsNewScopedUserAsync();
        var id = Guid.NewGuid();
        var code = "probe-" + id.ToString("N")[..12];

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.InsertMerchantAsync(scoped, id, code));
    }

    // REQ-3.7's broader "Scoped writing cross-scope is BLOCKed" claim, on a second table beyond Merchants: a
    // Scoped platform user's own assigned merchant still writes fine, but a merchant outside its
    // MerchantAccess rows is BLOCKed by the exact same predicate.
    [Fact]
    public async Task Scoped_admin_can_write_a_vault_secret_for_its_assigned_merchant_but_not_others()
    {
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();
        await using var scoped = await IntegrationDb.OpenAsNewScopedUserAsync(merchantA);

        await IntegrationDb.InsertVaultSecretAsync(scoped, merchantA, "probe-" + Guid.NewGuid().ToString("N")[..8], "key-1", "hint");

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertVaultSecretAsync(scoped, merchantB, "probe-" + Guid.NewGuid().ToString("N")[..8], "key-1", "hint"));
    }

    [Fact]
    public async Task Scoped_admin_sees_only_its_assigned_merchant()
    {
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantA))
            await IntegrationDb.InsertProductAsync(a, productA, merchantA);
        await using (var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantB))
            await IntegrationDb.InsertProductAsync(b, productB, merchantB);

        await using var scoped = await IntegrationDb.OpenAsNewScopedUserAsync(merchantA);

        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(scoped, "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", productA))));
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(scoped, "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", productB))));
    }

    // REQ-3.11 — the critical fail-closed regression guard for the F2 finding (requirements.md /spec-analyze log):
    // absence in MerchantAccess must NEVER be read as "unrestricted Super" (that would be fail-open). A
    // Scoped platform user with zero assignments must see nothing at all.
    [Fact]
    public async Task Scoped_admin_with_no_platform_merchant_access_rows_sees_nothing()
    {
        var merchantA = Guid.NewGuid();
        var productA = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchantA))
            await IntegrationDb.InsertProductAsync(a, productA, merchantA);

        await using var scoped = await IntegrationDb.OpenAsNewScopedUserAsync(); // zero MerchantAccess rows

        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(scoped, "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", productA))));
    }

    // REQ-3.4: the predicate's OWN guard against an empty sentinel with an explicit NULL user, tested
    // independently of the app-layer throw (REQ-4.3) that normally keeps this state from ever reaching SQL —
    // belt-and-suspenders in case something else ever stamps SESSION_CONTEXT directly.
    [Fact]
    public async Task Explicit_empty_sentinel_with_explicit_null_user_denies()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.MerchantA);

        await using var bare = await IntegrationDb.OpenWithUnboundEmptySentinelAsync(IntegrationDb.AdminConn);

        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(bare, "SELECT COUNT(*) FROM shop.Products WHERE Id=@id", ("@id", id))));
    }

    // REQ-3.5's write half: the read half (0 rows with no context at all) is already covered by
    // Admin_without_a_bound_platform_user_sees_nothing and Sysadmin_without_context_or_bypass_sees_nothing above.
    [Fact]
    public async Task No_context_at_all_blocks_insert_on_a_filtered_table()
    {
        await using var noCtx = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertProductAsync(noCtx, Guid.NewGuid(), Guid.NewGuid()));
    }

    // REQ-3.10: every schema the predicate touches (directly or by ownership-chained cross-schema reference)
    // must be owned by dbo, or the cross-schema SELECT inside sec.fn_merchant_predicate would need an explicit
    // grant pol_app/pol_admin never receive.
    [Fact]
    public async Task Every_rls_schema_is_owned_by_dbo()
    {
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.name, p.name FROM sys.schemas s
            JOIN sys.database_principals p ON s.principal_id = p.principal_id
            WHERE s.name IN (N'shop', N'txn', N'admin', N'merch', N'sec');
            """;
        await using var reader = await cmd.ExecuteReaderAsync();
        var owners = new Dictionary<string, string>();
        while (await reader.ReadAsync())
            owners[reader.GetString(0)] = reader.GetString(1);

        Assert.Equal(5, owners.Count);
        Assert.All(owners.Values, owner => Assert.Equal("dbo", owner));
    }

    // Proc 2 of 3 (proc 1 = Webhook_resolve_proc_returns_merchant_while_direct_read_stays_blocked above): mirrors
    // the real anonymous-customer path — the order is created by its own merchant, but the summary link is
    // resolved by an opaque token from a caller with NO merchant bound at all, via the pol_resolver bypass.
    [Fact]
    public async Task Order_summary_resolve_proc_finds_the_order_by_token_with_no_caller_context()
    {
        var merchant = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        string token;
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchant))
        {
            await IntegrationDb.InsertOrderAsync(a, orderId, merchant, status: 1, amount: 250.09m, currency: "THB");
            token = (string)(await IntegrationDb.ScalarAsync(a, "SELECT SummaryToken FROM shop.Orders WHERE Id=@id", ("@id", orderId)))!;
        }

        await using var noCtx = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var resolvedId = (Guid)(await IntegrationDb.ScalarAsync(noCtx, "EXEC sec.usp_resolve_order_summary @Token=@t", ("@t", token)))!;

        Assert.Equal(orderId, resolvedId);
    }

    // Proc 3 of 3: usp_vault_audit_head's sp_getapplock uses @LockOwner='Transaction', so the caller must hold an
    // explicit transaction open across the call — mirrors VaultRevealAuditWriter.AppendViaProcAsync exactly.
    [Fact]
    public async Task Vault_audit_head_proc_returns_the_chain_head()
    {
        var merchant = Guid.NewGuid();
        var revealedAt = DateTime.UtcNow;
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, merchant);
        var expectedHash = await IntegrationDb.InsertVaultRevealAuditAsync(conn, merchant, "psp-secret", revealedAt);

        using var tx = conn.BeginTransaction();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "EXEC sec.usp_vault_audit_head @MerchantId=@m";
        cmd.Parameters.AddWithValue("@m", merchant);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var lastSeq = reader.GetInt64(0);
        var lastHash = (byte[])reader["LastHash"];
        await reader.CloseAsync();
        tx.Commit();

        Assert.Equal(1L, lastSeq);
        Assert.Equal(expectedHash, lastHash);
    }
}
