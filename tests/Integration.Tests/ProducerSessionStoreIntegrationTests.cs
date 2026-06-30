using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// The set-based session transitions the producer store relies on, asserted against live SQL (REQ-11.5/11.3/10.4).
/// These mirror <c>ProducerSessionStore</c>'s ExecuteUpdate/ExecuteDelete statements: the single-winner supersede,
/// family revocation, logout-all (revoke-all-for-user), prune-by-absolute-expiry, and the control-plane grant
/// posture (pol_app cannot touch the tables). Tagged Integration: the default unit run skips them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProducerSessionStoreIntegrationTests
{
    private const int Active = 0, Superseded = 1, Revoked = 2;

    private static Task InsertSessionAsync(SqlConnection c, Guid id, Guid familyId, Guid userId, int status, int absHours) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT producer.ProducerSessions (Id, FamilyId, TokenHash, ProducerAccountId, Status, IssuedAt, IdleExpiresAt, AbsoluteExpiresAt)
            VALUES (@id, @fam, @hash, @user, @st, SYSUTCDATETIME(), DATEADD(MINUTE, 30, SYSUTCDATETIME()), DATEADD(HOUR, @abs, SYSUTCDATETIME()));
            """,
            ("@id", id), ("@fam", familyId), ("@hash", RandomNumberGenerator.GetBytes(32)),
            ("@user", userId), ("@st", status), ("@abs", absHours));

    private const string Supersede =
        "UPDATE producer.ProducerSessions SET Status=1, SupersededAt=SYSUTCDATETIME(), SupersededBySessionId=@s WHERE Id=@id AND Status=0";

    [Fact]
    public async Task Supersede_is_a_single_winner()
    {
        var id = Guid.NewGuid();
        await using var admin = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await InsertSessionAsync(admin, id, Guid.NewGuid(), Guid.NewGuid(), Active, 8);

        var first = await IntegrationDb.ExecAsync(admin, Supersede, ("@s", Guid.NewGuid()), ("@id", id));
        var second = await IntegrationDb.ExecAsync(admin, Supersede, ("@s", Guid.NewGuid()), ("@id", id));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task Revoke_family_revokes_every_non_revoked_member()
    {
        var family = Guid.NewGuid();
        var user = Guid.NewGuid();
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await InsertSessionAsync(conn, Guid.NewGuid(), family, user, Active, 8);
        await InsertSessionAsync(conn, Guid.NewGuid(), family, user, Superseded, 8);

        var revoked = await IntegrationDb.ExecAsync(conn,
            "UPDATE producer.ProducerSessions SET Status=2 WHERE FamilyId=@f AND Status<>2", ("@f", family));

        Assert.Equal(2, revoked);
        Assert.Equal(0, Convert.ToInt32(await IntegrationDb.ScalarAsync(conn,
            "SELECT COUNT(*) FROM producer.ProducerSessions WHERE FamilyId=@f AND Status<>2", ("@f", family))));
    }

    [Fact]
    public async Task Revoke_all_for_user_kills_every_device()
    {
        var user = Guid.NewGuid();
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await InsertSessionAsync(conn, Guid.NewGuid(), Guid.NewGuid(), user, Active, 8);      // device 1
        await InsertSessionAsync(conn, Guid.NewGuid(), Guid.NewGuid(), user, Active, 8);      // device 2

        var revoked = await IntegrationDb.ExecAsync(conn,
            "UPDATE producer.ProducerSessions SET Status=2 WHERE ProducerAccountId=@u AND Status<>2", ("@u", user));

        Assert.Equal(2, revoked);
    }

    [Fact]
    public async Task Prune_deletes_only_rows_past_their_absolute_expiry()
    {
        var live = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var user = Guid.NewGuid();
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AdminConn);
        await InsertSessionAsync(conn, live, Guid.NewGuid(), user, Active, 8);       // absolute 8h out
        await InsertSessionAsync(conn, expired, Guid.NewGuid(), user, Revoked, -1);  // absolute 1h ago

        var pruned = await IntegrationDb.ExecAsync(conn,
            "DELETE producer.ProducerSessions WHERE AbsoluteExpiresAt < SYSUTCDATETIME() AND Id IN (@a,@b)",
            ("@a", live), ("@b", expired));

        Assert.Equal(1, pruned);
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(conn,
            "SELECT COUNT(*) FROM producer.ProducerSessions WHERE Id=@id", ("@id", live))));
    }

    [Fact]
    public async Task App_principal_cannot_touch_the_session_tables()
    {
        await using var app = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);

        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerSessions"));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ScalarAsync(app, "SELECT COUNT(*) FROM producer.ProducerAuthAudits"));
    }
}
