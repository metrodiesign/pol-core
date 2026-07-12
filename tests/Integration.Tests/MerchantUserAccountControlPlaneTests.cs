using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Control-plane posture for the merchant-user ACCOUNT table against live SQL Server 2025 with the real
/// principals. merch.Users is NOT under the merchant RLS predicate (like Admin's Users):
/// pol_app has no grant at all. T5 collapsed the old one-tenant-per-producer edge table into a single
/// nullable MerchantId column directly on the account row (a PendingApproval account has no merchant yet;
/// approval sets it) — reachable only via the pol_admin connection.
/// Tagged Integration: the default unit run (Category!=Integration) skips it; CI runs it against a SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MerchantUserAccountControlPlaneTests
{
    private static int AsInt(object? o) => (int)o!;

    // MerchantUserStatus: PendingApproval=0, Active=1.
    private const int PendingApproval = 0;
    private const int Active = 1;

    private const string InsertAccount =
        "INSERT merch.Users (Id, Subject, Email, Status, MerchantId, DisplayName, FirstName, LastName, CreatedAt) " +
        "VALUES (@id, @sub, @email, @status, @merchant, N'Name', N'First', N'Last', SYSUTCDATETIME())";

    private static (string, object)[] AccountArgs(Guid id, string subject, int status, Guid? merchantId = null) =>
    [
        ("@id", id),
        ("@sub", subject),
        ("@email", subject + "@org.com"),
        ("@status", status),
        ("@merchant", (object?)merchantId ?? DBNull.Value),
    ];

    [Fact]
    public async Task App_cannot_read_the_control_plane_account_table()
    {
        // pol_app has NO grant on merch.Users (control-plane, like Admin) — even a SELECT is refused.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM merch.Users"));
    }

    [Fact]
    public async Task Admin_can_insert_and_read_a_merchant_user_account()
    {
        // pol_admin owns the control-plane realm: registration/approval writes + reads the account cross-merchant.
        var id = Guid.NewGuid();
        var subject = "cp-account-" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        var rows = await IntegrationDb.ExecAsync(admin, InsertAccount, AccountArgs(id, subject, PendingApproval));
        Assert.Equal(1, rows);

        var seen = AsInt(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM merch.Users WHERE Id=@id", ("@id", id)));
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task Approving_an_account_sets_its_merchant()
    {
        // T5: approval is a plain UPDATE of the nullable MerchantId column (no separate assignment edge/unique
        // index anymore — a single row can only ever carry one merchant).
        var id = Guid.NewGuid();
        var subject = "cp-approve-" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await IntegrationDb.ExecAsync(admin, InsertAccount, AccountArgs(id, subject, PendingApproval));

        await IntegrationDb.ExecAsync(admin,
            "UPDATE merch.Users SET Status=@st, MerchantId=@m WHERE Id=@id",
            ("@st", Active), ("@m", IntegrationDb.MerchantA), ("@id", id));

        Assert.Equal(IntegrationDb.MerchantA, await IntegrationDb.ScalarAsync(admin,
            "SELECT MerchantId FROM merch.Users WHERE Id=@id", ("@id", id)));
    }

    [Fact]
    public async Task A_second_account_for_the_same_subject_is_rejected_by_the_unique_index()
    {
        // One record per subject (REQ-1.4/4.6): the UNIQUE index on Users.Subject is what makes a replayed
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
        // The account and the identity child tables are all control-plane: pol_app has no grant at all, so even a
        // SELECT is refused.
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM merch.Users"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM merch.ExternalLogins"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM merch.RegistrationAudits"));
    }

    [Fact]
    public async Task Worker_can_insert_and_read_registration_notices_but_app_cannot()
    {
        // merch.RegistrationNotices is the outbox-consumer notice table (S5): pol_worker (NOT a bypass member)
        // gets INSERT/SELECT so the idempotent consumer can record a notice; pol_app gets nothing.
        var id = Guid.NewGuid();
        var merchantUserId = Guid.NewGuid();
        const string insert =
            "INSERT merch.RegistrationNotices (Id, MerchantUserId, Subject, Email, DisplayName, OccurredAt, CreatedAt) " +
            "VALUES (@id, @mu, N'sub', N's@org.com', N'Name', SYSUTCDATETIME(), SYSUTCDATETIME())";

        await using (var worker = await IntegrationDb.OpenAsync(IntegrationDb.WorkerConn))
        {
            await IntegrationDb.ExecAsync(worker, insert, ("@id", id), ("@mu", merchantUserId));
            var seen = AsInt(await IntegrationDb.ScalarAsync(worker,
                "SELECT COUNT(*) FROM merch.RegistrationNotices WHERE Id=@id", ("@id", id)));
            Assert.Equal(1, seen);
        }

        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn, IntegrationDb.MerchantA);
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM merch.RegistrationNotices"));
    }
}
