using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Constraint floor for the merchant-user ACCOUNT table against live SQL Server 2025 with the
/// RlsTeardownAndOnePrincipal migration applied. T5 collapsed the old one-tenant-per-producer edge table into
/// a single nullable MerchantId column directly on the account row (a PendingApproval account has no
/// merchant yet; approval sets it). Updated for rls-to-query-filter task 8: pol_admin/pol_worker are
/// retired — pol_app is the sole runtime principal and now holds full CRUD on merch.Users/ExternalLogins/
/// RegistrationAudits/RegistrationNotices (it IS the control-plane's own connection in production), so the
/// old "pol_app/pol_worker cannot touch these tables" grant-separation tests are gone — there is only one
/// principal left. Tagged Integration: the default unit run (Category!=Integration) skips it; CI runs it
/// against a SQL service.
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
    public async Task Admin_can_insert_and_read_a_merchant_user_account()
    {
        var id = Guid.NewGuid();
        var subject = "cp-account-" + Guid.NewGuid().ToString("N")[..8];
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

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
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
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
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        await IntegrationDb.ExecAsync(admin, InsertAccount, AccountArgs(Guid.NewGuid(), subject, PendingApproval));

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(admin, InsertAccount,
            AccountArgs(Guid.NewGuid(), subject, PendingApproval)));
    }
}
