using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// The set-based session transitions the store relies on, asserted against live SQL (REQ-5.5/5.3/11.5). These
/// mirror <c>SessionStore</c>'s ExecuteUpdate/ExecuteDelete statements: the single-winner supersede
/// (only one of two conditional UPDATEs touches an Active row), family revocation, prune-by-absolute-expiry, and
/// the "at most one Active per family" invariant. Tagged Integration: the default unit run skips them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminSessionStoreIntegrationTests
{
    private const int Active = 0, Superseded = 1, Revoked = 2;

    private static Task InsertSessionAsync(SqlConnection c, Guid id, Guid familyId, Guid adminId, int status, int absHours) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT admin.Sessions (Id, FamilyId, TokenHash, AdminUserId, Status, IssuedAt, IdleExpiresAt, AbsoluteExpiresAt)
            VALUES (@id, @fam, @hash, @admin, @st, SYSUTCDATETIME(), DATEADD(MINUTE, 30, SYSUTCDATETIME()), DATEADD(HOUR, @abs, SYSUTCDATETIME()));
            """,
            ("@id", id), ("@fam", familyId), ("@hash", RandomNumberGenerator.GetBytes(32)),
            ("@admin", adminId), ("@st", status), ("@abs", absHours));

    private const string Supersede =
        "UPDATE admin.Sessions SET Status=1, SupersededAt=SYSUTCDATETIME(), SupersededBySessionId=@s WHERE Id=@id AND Status=0";

    [Fact]
    public async Task Supersede_is_a_single_winner()
    {
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(admin, id, Guid.NewGuid(), Guid.NewGuid(), Active, 8);

        // Two conditional updates race for the one Active row; only the first matches Status=0 (REQ-5.5).
        var first = await IntegrationDb.ExecAsync(admin, Supersede, ("@s", Guid.NewGuid()), ("@id", id));
        var second = await IntegrationDb.ExecAsync(admin, Supersede, ("@s", Guid.NewGuid()), ("@id", id));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Revoke_family_revokes_every_non_revoked_member()
    {
        var family = Guid.NewGuid();
        var admin = Guid.NewGuid();
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(conn, Guid.NewGuid(), family, admin, Active, 8);
        await InsertSessionAsync(conn, Guid.NewGuid(), family, admin, Superseded, 8);

        var revoked = await IntegrationDb.ExecAsync(conn,
            "UPDATE admin.Sessions SET Status=2 WHERE FamilyId=@f AND Status<>2", ("@f", family));

        Assert.Equal(2, revoked);
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(conn,
            "SELECT COUNT(*) FROM admin.Sessions WHERE FamilyId=@f AND Status<>2", ("@f", family))));
    }

    [Fact]
    public async Task Prune_deletes_only_rows_past_their_absolute_expiry()
    {
        var live = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var admin = Guid.NewGuid();
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(conn, live, Guid.NewGuid(), admin, Active, 8);       // absolute 8h out
        await InsertSessionAsync(conn, expired, Guid.NewGuid(), admin, Revoked, -1);  // absolute 1h ago

        var deleted = await IntegrationDb.ExecAsync(conn,
            "DELETE admin.Sessions WHERE AbsoluteExpiresAt < SYSUTCDATETIME() AND Id IN (@a,@b)",
            ("@a", live), ("@b", expired));

        Assert.Equal(1, deleted);
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(conn,
            "SELECT COUNT(*) FROM admin.Sessions WHERE Id=@id", ("@id", live))));   // live remains
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(conn,
            "SELECT COUNT(*) FROM admin.Sessions WHERE Id=@id", ("@id", expired)))); // expired gone
    }

    [Fact]
    public async Task A_family_has_at_most_one_active_session()
    {
        var family = Guid.NewGuid();
        var admin = Guid.NewGuid();
        var active = Guid.NewGuid();
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(conn, active, family, admin, Active, 8);
        await InsertSessionAsync(conn, Guid.NewGuid(), family, admin, Superseded, 8);

        // Exactly one Active -> the store returns that id.
        Assert.Equal(active, await IntegrationDb.ScalarAsync(conn,
            "SELECT Id FROM admin.Sessions WHERE FamilyId=@f AND Status=0", ("@f", family)));

        // A second Active in the same family -> the store's Take(2) count != 1 -> "no single active" (fail-closed).
        await InsertSessionAsync(conn, Guid.NewGuid(), family, admin, Active, 8);
        Assert.Equal(2, Convert.ToInt32(await IntegrationDb.ScalarAsync(conn,
            "SELECT COUNT(*) FROM admin.Sessions WHERE FamilyId=@f AND Status=0", ("@f", family))));
    }
}
