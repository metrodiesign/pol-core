using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the multi-tenant isolation floor (PLAN #3) against live SQL Server 2025 with the real
/// principals and security policy. These are the regression guards for the RLS migration: if a future
/// change weakens a predicate, a grant, or the resolve proc, one of these fails. Tagged Integration so
/// the default unit run (Category!=Integration) skips them; CI runs them against a SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RlsIsolationTests
{
    private static int AsInt(object? o) => (int)o!;

    [Fact]
    public async Task TenantA_row_is_invisible_to_TenantB()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.TenantA);

        await using var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantB);
        var seen = AsInt(await IntegrationDb.ScalarAsync(b,
            "SELECT COUNT(*) FROM producer.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task TenantA_can_see_its_own_row()
    {
        var id = Guid.NewGuid();
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.TenantA);

        var seen = AsInt(await IntegrationDb.ScalarAsync(a,
            "SELECT COUNT(*) FROM producer.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Writing_a_foreign_tenant_id_is_blocked()
    {
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        // Block predicate (AFTER INSERT): a tenant cannot stamp another tenant's id onto its rows.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertProductAsync(a, Guid.NewGuid(), IntegrationDb.TenantB));
    }

    [Fact]
    public async Task Admin_sees_every_tenant()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.TenantA);

        // pol_admin is in pol_rls_bypass: no SESSION_CONTEXT needed, sees the row.
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        var seen = AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Sysadmin_without_context_or_bypass_sees_nothing()
    {
        var id = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA))
            await IntegrationDb.InsertProductAsync(a, id, IntegrationDb.TenantA);

        // Proven on live SQL: RLS applies even to sysadmin/dbo. The predicate is the sole authority.
        await using var sa = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        var seen = AsInt(await IntegrationDb.ScalarAsync(sa,
            "SELECT COUNT(*) FROM producer.Products WHERE Id=@id", ("@id", id)));

        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task App_cannot_read_outbox_payloads()
    {
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        // pol_app has INSERT but NO SELECT on the outbox -> it can never read another tenant's payload.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(a, "SELECT COUNT(*) FROM producer.OutboxMessages"));
    }

    [Fact]
    public async Task App_cannot_forge_a_foreign_tenant_outbox_row()
    {
        await using var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        const string insert =
            "INSERT producer.OutboxMessages (Id,TenantId,Type,Payload,OccurredAtUtc,Attempts) " +
            "VALUES (@id,@t,N'T',N'{}',SYSUTCDATETIME(),0)";

        // Own tenant: allowed.
        await IntegrationDb.ExecAsync(a, insert, ("@id", Guid.NewGuid()), ("@t", IntegrationDb.TenantA));

        // Foreign tenant: blocked by the BLOCK-after-insert predicate.
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(a, insert, ("@id", Guid.NewGuid()), ("@t", IntegrationDb.TenantB)));
    }

    [Fact]
    public async Task Webhook_resolve_proc_returns_tenant_while_direct_read_stays_blocked()
    {
        // Fresh tenant per run: PspConnections has a UNIQUE (TenantId, Psp) index, so reusing a fixed
        // tenant would collide on re-runs against a persistent dev database.
        var tenant = Guid.NewGuid();
        var connId = Guid.NewGuid();
        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, tenant))
            await IntegrationDb.InsertPspConnectionAsync(a, connId, tenant);

        // pol_app with NO bound tenant: it cannot read the connection directly...
        await using var noCtx = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var direct = AsInt(await IntegrationDb.ScalarAsync(noCtx,
            "SELECT COUNT(*) FROM producer.PspConnections WHERE Id=@id", ("@id", connId)));
        Assert.Equal(0, direct);

        // ...but the EXECUTE-AS-bypass proc resolves its tenant id.
        var resolved = (Guid)(await IntegrationDb.ScalarAsync(noCtx,
            "EXEC producer.usp_resolve_webhook_tenant @PspConnectionId=@id", ("@id", connId)))!;
        Assert.Equal(tenant, resolved);
    }

    [Fact]
    public async Task Host_principals_have_the_expected_bypass_membership()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        Assert.Equal("pol_app", (string)(await IntegrationDb.ScalarAsync(app, "SELECT SUSER_SNAME()"))!);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(app, "SELECT IS_ROLEMEMBER('pol_rls_bypass')")));

        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(admin, "SELECT IS_ROLEMEMBER('pol_rls_bypass')")));

        await using var worker = await IntegrationDb.OpenAsync(IntegrationDb.WorkerConn);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(worker, "SELECT IS_ROLEMEMBER('pol_rls_bypass')")));
    }
}
