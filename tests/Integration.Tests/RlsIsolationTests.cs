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
}
