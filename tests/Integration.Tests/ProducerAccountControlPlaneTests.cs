using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Control-plane posture for the producer ACCOUNT tables against live SQL Server 2025 with the real principals.
/// After the Admin-parity move, producer.ProducerAccounts is NOT under the tenant RLS predicate (like Admin's
/// AdminAccounts): pol_app has no grant at all, and the tenant a producer acts for lives on a separate
/// producer.ProducerTenantAssignments edge (UNIQUE on ProducerAccountId — one tenant per producer). A producer
/// account/assignment is therefore reachable only via the pol_admin connection.
/// Tagged Integration: the default unit run (Category!=Integration) skips it; CI runs it against a SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProducerAccountControlPlaneTests
{
    private static int AsInt(object? o) => (int)o!;

    // ProducerAccountStatus: PendingApproval=0, Active=1.
    private const int PendingApproval = 0;
    private const int Active = 1;

    private const string InsertAccount =
        "INSERT producer.ProducerAccounts (Id, Subject, Email, Status, CreatedAt) " +
        "VALUES (@id, @sub, @email, @status, SYSUTCDATETIME())";

    private const string InsertAssignment =
        "INSERT producer.ProducerTenantAssignments (Id, ProducerAccountId, TenantId, AssignedByAdminId, AssignedAt) " +
        "VALUES (@id, @acc, @tenant, @admin, SYSUTCDATETIME())";

    private static (string, object)[] AccountArgs(Guid id, string subject, int status) =>
    [
        ("@id", id),
        ("@sub", subject),
        ("@email", subject + "@org.com"),
        ("@status", status),
    ];

    [Fact]
    public async Task App_cannot_read_the_control_plane_account_table()
    {
        // pol_app has NO grant on producer.ProducerAccounts (control-plane, like Admin) — even a SELECT is refused.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerAccounts"));
    }

    [Fact]
    public async Task Admin_can_insert_and_read_a_producer_account()
    {
        // pol_admin owns the control-plane realm: registration/approval writes + reads the account cross-tenant.
        var id = Guid.NewGuid();
        var subject = "cp-account-" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        var rows = await IntegrationDb.ExecAsync(admin, InsertAccount, AccountArgs(id, subject, PendingApproval));
        Assert.Equal(1, rows);

        var seen = AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM producer.ProducerAccounts WHERE Id=@id", ("@id", id)));
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task App_cannot_read_the_tenant_assignment_table()
    {
        // The tenant edge is control-plane too: pol_app has no grant.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerTenantAssignments"));
    }

    [Fact]
    public async Task A_second_tenant_for_the_same_account_is_rejected_by_the_unique_index()
    {
        // One tenant per producer (REQ-6): the UNIQUE index on ProducerAccountId rejects a second assignment.
        var accountId = Guid.NewGuid();
        var subject = "cp-onetenant-" + Guid.NewGuid().ToString("N")[..8];
        var admin0 = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        await IntegrationDb.ExecAsync(admin, InsertAccount, AccountArgs(accountId, subject, Active));
        await IntegrationDb.ExecAsync(admin, InsertAssignment,
            ("@id", Guid.NewGuid()), ("@acc", accountId), ("@tenant", IntegrationDb.TenantA), ("@admin", admin0));

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin, InsertAssignment,
            ("@id", Guid.NewGuid()), ("@acc", accountId), ("@tenant", IntegrationDb.TenantB), ("@admin", admin0)));
    }

    [Fact]
    public async Task A_second_account_for_the_same_subject_is_rejected_by_the_unique_index()
    {
        // One record per subject (REQ-1.4/4.6): the UNIQUE index on ProducerAccounts.Subject is what makes a replayed
        // still-valid registration token (or a concurrent second submit) a 409 instead of a duplicate row — the
        // duplicate-registration guarantee the stateless-ticket redesign leans on. Person details now live on the
        // account itself, so this is the single guard.
        var subject = "cp-onesubject-" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        await IntegrationDb.ExecAsync(admin, InsertAccount, AccountArgs(Guid.NewGuid(), subject, PendingApproval));

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin, InsertAccount,
            AccountArgs(Guid.NewGuid(), subject, PendingApproval)));
    }

    [Fact]
    public async Task App_cannot_touch_the_control_plane_tables()
    {
        // The account, the tenant edge, and the identity child tables are all control-plane: pol_app has no grant at
        // all, so even a SELECT is refused.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.TenantA);

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerAccounts"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerTenantAssignments"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ExternalLogins"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.RegistrationAudits"));
    }

    [Fact]
    public async Task Worker_can_insert_and_read_registration_notices_but_app_cannot()
    {
        // ProducerRegistrationNotices is the outbox-consumer notice table (S5): pol_worker (NOT a bypass member)
        // gets INSERT/SELECT so the idempotent consumer can record a notice; pol_app gets nothing. The notice keeps
        // its TenantUserId column (the registration-event identity, out of scope for the account rename).
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
