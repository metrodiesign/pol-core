using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Proves the Identity (TenantUser realm) isolation + grant floor against live SQL Server 2025 with the real
/// principals and the AddIdentityTables migration applied. Regression guards for that migration's RLS
/// predicate (FILTER+BLOCK on fn_tenant_predicate(TenantId) on TenantUsers) and grants: a tenant reads only
/// its own users, a pending (NULL-tenant) applicant is invisible to every tenant but visible to pol_admin,
/// pol_app cannot write the identity tables, the child tables are admin-only, and a duplicate subject is
/// rejected. Tagged Integration so the default unit run skips them; CI runs them against a migrated service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IdentityIsolationIntegrationTests
{
    private const int Active = 1;
    private const int PendingApproval = 0;

    private static int AsInt(object? o) => (int)o!;
    private static string UniqueSubject() => "g-" + Guid.NewGuid().ToString("N")[..16];

    [Fact]
    public async Task Admin_can_insert_and_read_tenant_users_cross_tenant()
    {
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.InsertTenantUserAsync(admin, id, UniqueSubject(), IntegrationDb.TenantA, Active);

        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task Tenant_reads_only_its_own_users()
    {
        // An active user assigned to TenantA is visible to a pol_app connection bound to TenantA, and
        // filtered to zero for a connection bound to any other tenant (FILTER on TenantId) — REQ-8.2/8.3.
        var id = Guid.NewGuid();
        await using (var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn))
            await IntegrationDb.InsertTenantUserAsync(admin, id, UniqueSubject(), IntegrationDb.TenantA, Active);

        await using var own = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(own,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id))));

        await using var other = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantB);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(other,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task Pending_users_are_invisible_to_a_tenant_but_visible_to_admin()
    {
        // A pending applicant has TenantId NULL: NULL = SESSION_CONTEXT is UNKNOWN, so the FILTER hides it
        // from every tenant; only the pol_admin bypass sees it (to approve it) — REQ-6.3 / 8.2.
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.InsertTenantUserAsync(admin, id, UniqueSubject(), tenantId: null, PendingApproval);

        await using var tenant = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        Assert.Equal(0, AsInt(await IntegrationDb.ScalarAsync(tenant,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id))));

        Assert.Equal(1, AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task App_cannot_write_tenant_users()
    {
        // pol_app has SELECT only on TenantUsers (no INSERT grant); the BLOCK predicate is belt-and-braces.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertTenantUserAsync(app, Guid.NewGuid(), UniqueSubject(), IntegrationDb.TenantA, Active));
    }

    [Fact]
    public async Task Child_identity_tables_are_admin_only()
    {
        // ExternalLogins/Profiles/RegistrationTickets/RegistrationAudits have NO pol_app grant — a tenant
        // principal cannot touch them at all; pol_admin (the resolve/registration path) reads them — REQ-8.4.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ExternalLogins"));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.RegistrationTickets"));

        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        Assert.True(AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.RegistrationAudits")) >= 0); // readable (no throw) is the assertion
    }

    [Fact]
    public async Task Duplicate_subject_is_rejected()
    {
        // The unique index on Subject stops the same Google identity from registering twice — REQ-4.5.
        var subject = UniqueSubject();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.InsertTenantUserAsync(admin, Guid.NewGuid(), subject, tenantId: null, PendingApproval);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.InsertTenantUserAsync(admin, Guid.NewGuid(), subject, tenantId: null, PendingApproval));
    }
}
