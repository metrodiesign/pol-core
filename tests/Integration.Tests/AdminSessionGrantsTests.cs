using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Grant posture for the admin BFF session tables (REQ-11.1/11.2/12.2), updated for rls-to-query-filter task 8
/// (RlsTeardownAndOnePrincipal): pol_admin is retired — pol_app is the sole runtime principal and now holds
/// exactly the grants pol_admin used to hold on these two tables. Sessions is full CRUD (rotate/revoke =
/// UPDATE, prune = DELETE); AuthAudits is append-only (SELECT, INSERT — no UPDATE/DELETE). The old
/// "pol_app cannot read these tables at all" separation test is gone — there is only one principal left, so
/// there is nothing left to separate it from. Tagged Integration: the default unit run skips them; CI runs
/// against live SQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminSessionGrantsTests
{
    private static async Task InsertSessionAsync(SqlConnection c, Guid id)
    {
        await IntegrationDb.ExecAsync(c,
            """
            INSERT admin.Sessions (Id, FamilyId, TokenHash, PlatformUserId, Status, IssuedAt, IdleExpiresAt, AbsoluteExpiresAt)
            VALUES (@id, @fam, @hash, @admin, 0, SYSUTCDATETIME(), DATEADD(MINUTE, 30, SYSUTCDATETIME()), DATEADD(HOUR, 8, SYSUTCDATETIME()));
            """,
            ("@id", id), ("@fam", Guid.NewGuid()), ("@hash", RandomNumberGenerator.GetBytes(32)), ("@admin", Guid.NewGuid()));
    }

    [Fact]
    public async Task PolApp_can_insert_select_update_and_delete_sessions()
    {
        var id = Guid.NewGuid();
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        await InsertSessionAsync(app, id);
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(app,
            "SELECT COUNT(*) FROM admin.Sessions WHERE Id=@id", ("@id", id))));

        // rotate/revoke == UPDATE, prune == DELETE — both granted.
        await IntegrationDb.ExecAsync(app, "UPDATE admin.Sessions SET Status=2 WHERE Id=@id", ("@id", id));
        await IntegrationDb.ExecAsync(app, "DELETE admin.Sessions WHERE Id=@id", ("@id", id));
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(app,
            "SELECT COUNT(*) FROM admin.Sessions WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task Auth_audits_are_append_only_for_pol_app()
    {
        var id = Guid.NewGuid();
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        // INSERT + SELECT are granted...
        await IntegrationDb.ExecAsync(app,
            """
            INSERT admin.AuthAudits (Id, EventType, CorrelationId, OccurredAt)
            VALUES (@id, N'login-success', N'corr-1', SYSUTCDATETIME());
            """, ("@id", id));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(app,
            "SELECT COUNT(*) FROM admin.AuthAudits WHERE Id=@id", ("@id", id))));

        // ...but UPDATE and DELETE are NOT (append-only, REQ-12.2).
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(app, "UPDATE admin.AuthAudits SET Reason=N'x' WHERE Id=@id", ("@id", id)));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(app, "DELETE admin.AuthAudits WHERE Id=@id", ("@id", id)));
    }
}
