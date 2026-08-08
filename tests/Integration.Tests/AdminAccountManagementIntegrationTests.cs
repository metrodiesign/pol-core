using System.Security.Cryptography;
using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// The admin session-management store SQL, asserted against live SQL (admin-account-management REQ-4/5):
/// <c>ListByAdminAsync</c> scopes to one admin and orders newest-first + id tiebreak, and a family revoke touches
/// ONLY the target family (other families of the SAME admin survive — REQ-5.1 revokes a family, not the whole
/// account). Mirrors <c>SessionStore.ListByAdminAsync</c> / <c>RevokeFamilyAsync</c>. Tagged
/// Integration: the default unit run skips these.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AdminAccountManagementIntegrationTests
{
    private const int Active = 0, Revoked = 2;

    private static Task InsertSessionAsync(SqlConnection c, Guid id, Guid familyId, Guid adminId, int status, DateTime issuedAt) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT admin.Sessions (Id, FamilyId, TokenHash, AdminUserId, Status, IssuedAt, IdleExpiresAt, AbsoluteExpiresAt)
            VALUES (@id, @fam, @hash, @admin, @st, @issued, DATEADD(MINUTE, 30, @issued), DATEADD(HOUR, 8, @issued));
            """,
            ("@id", id), ("@fam", familyId), ("@hash", RandomNumberGenerator.GetBytes(32)),
            ("@admin", adminId), ("@st", status), ("@issued", issuedAt));

    [Fact]
    public async Task ListByAdmin_returns_only_that_admin_newest_first()
    {
        var adminA = Guid.NewGuid();
        var adminB = Guid.NewGuid();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(conn, older, Guid.NewGuid(), adminA, Active, now.AddHours(-2));
        await InsertSessionAsync(conn, newer, Guid.NewGuid(), adminA, Active, now.AddHours(-1));
        await InsertSessionAsync(conn, Guid.NewGuid(), Guid.NewGuid(), adminB, Active, now);   // a different admin

        // Mirror ListByAdminAsync: WHERE AdminUserId=@a ORDER BY IssuedAt DESC, Id.
        var ids = new List<Guid>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Id FROM admin.Sessions WHERE AdminUserId=@a ORDER BY IssuedAt DESC, Id";
            cmd.Parameters.AddWithValue("@a", adminA);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        }

        Assert.Equal(new[] { newer, older }, ids);   // only admin A, newest first
    }

    [Fact]
    public async Task Family_revoke_leaves_other_families_of_the_same_admin_untouched()
    {
        var admin = Guid.NewGuid();
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertSessionAsync(conn, Guid.NewGuid(), target, admin, Active, now);
        await InsertSessionAsync(conn, Guid.NewGuid(), other, admin, Active, now);

        var revoked = await IntegrationDb.ExecAsync(conn,
            "UPDATE admin.Sessions SET Status=@rev WHERE FamilyId=@f AND Status<>@rev",
            ("@rev", Revoked), ("@f", target));

        Assert.Equal(1, revoked);
        // The OTHER family is still Active — a family revoke does not touch the account's other families (REQ-5.1).
        Assert.Equal(1, Convert.ToInt32(await IntegrationDb.ScalarAsync(conn,
            "SELECT COUNT(*) FROM admin.Sessions WHERE FamilyId=@f AND Status=@act", ("@f", other), ("@act", Active))));
    }
}
