using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// Grant posture for the admin BFF session tables (REQ-11.1/11.2/12.2). PlatformUserSessions is full CRUD for
/// pol_admin (rotate/revoke = UPDATE, prune = DELETE); PlatformAuthAudits is append-only (SELECT, INSERT — no
/// UPDATE/DELETE); neither is granted to pol_app. Tagged Integration: the default unit run skips them; CI runs
/// against live SQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminSessionGrantsTests
{
    private static async Task InsertSessionAsync(SqlConnection c, Guid id)
    {
        await IntegrationDb.ExecAsync(c,
            """
            INSERT admin.PlatformUserSessions (Id, FamilyId, TokenHash, PlatformUserId, Status, IssuedAt, IdleExpiresAt, AbsoluteExpiresAt)
            VALUES (@id, @fam, @hash, @admin, 0, SYSUTCDATETIME(), DATEADD(MINUTE, 30, SYSUTCDATETIME()), DATEADD(HOUR, 8, SYSUTCDATETIME()));
            """,
            ("@id", id), ("@fam", Guid.NewGuid()), ("@hash", RandomNumberGenerator.GetBytes(32)), ("@admin", Guid.NewGuid()));
    }

    [Fact]
    public async Task Admin_can_insert_select_update_and_delete_sessions()
    {
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        await InsertSessionAsync(admin, id);
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.PlatformUserSessions WHERE Id=@id", ("@id", id))));

        // rotate/revoke == UPDATE, prune == DELETE — both granted.
        await IntegrationDb.ExecAsync(admin, "UPDATE admin.PlatformUserSessions SET Status=2 WHERE Id=@id", ("@id", id));
        await IntegrationDb.ExecAsync(admin, "DELETE admin.PlatformUserSessions WHERE Id=@id", ("@id", id));
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.PlatformUserSessions WHERE Id=@id", ("@id", id))));
    }

    [Fact]
    public async Task Auth_audits_are_append_only_for_admin()
    {
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);

        // INSERT + SELECT are granted...
        await IntegrationDb.ExecAsync(admin,
            """
            INSERT admin.PlatformAuthAudits (Id, EventType, CorrelationId, OccurredAt)
            VALUES (@id, N'login-success', N'corr-1', SYSUTCDATETIME());
            """, ("@id", id));
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(admin,
            "SELECT COUNT(*) FROM admin.PlatformAuthAudits WHERE Id=@id", ("@id", id))));

        // ...but UPDATE and DELETE are NOT (append-only, REQ-12.2).
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(admin, "UPDATE admin.PlatformAuthAudits SET Reason=N'x' WHERE Id=@id", ("@id", id)));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ExecAsync(admin, "DELETE admin.PlatformAuthAudits WHERE Id=@id", ("@id", id)));
    }

    [Fact]
    public async Task App_principal_cannot_read_the_control_plane_session_tables()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.PlatformUserSessions"));
        await Assert.ThrowsAsync<SqlException>(() =>
            IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM admin.PlatformAuthAudits"));
    }
}
