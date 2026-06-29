using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// RLS posture for the producer identity tables (producer-google-sso REQ-19) against live SQL Server 2025 with
/// the real principals and the TenantUsers security predicate. TenantUsers is the one RLS-keyed producer table
/// (FILTER+BLOCK on TenantId): a PendingApproval row (TenantId NULL) cannot be inserted under a tenant
/// (pol_app) SESSION_CONTEXT — NULL = SESSION_CONTEXT is UNKNOWN, so the BLOCK-after-insert predicate rejects it
/// — and MUST be written on the pol_admin (bypass) connection; an approved row is visible only to its own tenant.
/// Tagged Integration: the default unit run (Category!=Integration) skips it; CI runs it against a SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProducerIdentityRlsTests
{
    private static int AsInt(object? o) => (int)o!;

    private const string InsertTenantUser =
        "INSERT producer.TenantUsers (Id, Subject, Email, TenantId, Status, CreatedAt) " +
        "VALUES (@id, @sub, @email, @tenant, @status, SYSUTCDATETIME())";

    private static (string, object)[] UserArgs(Guid id, string subject, Guid? tenantId, int status) =>
    [
        ("@id", id),
        ("@sub", subject),
        ("@email", subject + "@org.com"),
        ("@tenant", (object?)tenantId ?? DBNull.Value),
        ("@status", status),
    ];

    // TenantUserStatus: PendingApproval=0, Active=1.
    private const int PendingApproval = 0;
    private const int Active = 1;

    [Fact]
    public async Task Pending_user_with_null_tenant_is_rejected_under_pol_app()
    {
        // A tenant principal cannot insert a NULL-TenantId (pre-approval) row: NULL fails the BLOCK predicate.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(app, InsertTenantUser,
            UserArgs(Guid.NewGuid(), "rls-pending-" + Guid.NewGuid().ToString("N")[..8], tenantId: null, PendingApproval)));
    }

    [Fact]
    public async Task Pending_user_with_null_tenant_is_allowed_under_pol_admin()
    {
        // pol_admin is in pol_rls_bypass: registration writes the pre-approval row tenant-less (REQ-19.2).
        var id = Guid.NewGuid();
        var subject = "rls-admin-pending-" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        var rows = await IntegrationDb.ExecAsync(admin, InsertTenantUser,
            UserArgs(id, subject, tenantId: null, PendingApproval));
        Assert.Equal(1, rows);

        var seen = AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id)));
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task A_null_tenant_pending_row_is_invisible_to_every_tenant()
    {
        // Insert a pre-approval row via the admin bypass, then prove a tenant principal cannot see it (the FILTER
        // hides NULL-TenantId rows from pol_app — REQ-19.3).
        var id = Guid.NewGuid();
        var subject = "rls-hidden-" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn))
            await IntegrationDb.ExecAsync(admin, InsertTenantUser, UserArgs(id, subject, tenantId: null, PendingApproval));

        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        var seen = AsInt(await IntegrationDb.ScalarAsync(app,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id)));
        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task An_approved_row_is_visible_only_to_its_own_tenant()
    {
        // Insert an Active row bound to TenantA via the admin bypass (a tenant principal cannot stamp TenantA
        // itself for another tenant). Its own tenant sees it; another tenant does not.
        var id = Guid.NewGuid();
        var subject = "rls-active-" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn))
            await IntegrationDb.ExecAsync(admin, InsertTenantUser, UserArgs(id, subject, IntegrationDb.TenantA, Active));

        await using (var a = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA))
        {
            var seenByOwner = AsInt(await IntegrationDb.ScalarAsync(a,
                "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id)));
            Assert.Equal(1, seenByOwner);
        }

        await using var b = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantB);
        var seenByOther = AsInt(await IntegrationDb.ScalarAsync(b,
            "SELECT COUNT(*) FROM producer.TenantUsers WHERE Id=@id", ("@id", id)));
        Assert.Equal(0, seenByOther);
    }

    [Fact]
    public async Task App_cannot_touch_the_control_plane_child_tables()
    {
        // ExternalLogins/RegistrationTickets/TenantUserProfiles/RegistrationAudits are control-plane: pol_app has
        // no grant at all, so even a SELECT is refused (REQ-19.4).
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ExternalLogins"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.RegistrationTickets"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.TenantUserProfiles"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.RegistrationAudits"));
    }

    [Fact]
    public async Task Worker_can_insert_and_read_registration_notices_but_app_cannot()
    {
        // ProducerRegistrationNotices is the outbox-consumer notice table (S5): pol_worker (NOT a bypass member)
        // gets INSERT/SELECT so the idempotent consumer can record a notice; pol_app gets nothing.
        var id = Guid.NewGuid();
        var tenantUserId = Guid.NewGuid();
        const string insert =
            "INSERT producer.ProducerRegistrationNotices (Id, TenantUserId, Subject, Email, DisplayName, OccurredAt, CreatedAt) " +
            "VALUES (@id, @tu, N'sub', N's@org.com', N'Name', SYSUTCDATETIME(), SYSUTCDATETIME())";

        await using (var worker = await IntegrationDb.OpenAsync(IntegrationDb.WorkerConn))
        {
            await IntegrationDb.ExecAsync(worker, insert, ("@id", id), ("@tu", tenantUserId));
            var seen = AsInt(await IntegrationDb.ScalarAsync(worker,
                "SELECT COUNT(*) FROM producer.ProducerRegistrationNotices WHERE Id=@id", ("@id", id)));
            Assert.Equal(1, seen);
        }

        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerRegistrationNotices"));
    }
}
