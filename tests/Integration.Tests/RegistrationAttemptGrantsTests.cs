using Microsoft.Data.SqlClient;

namespace Integration.Tests;

/// <summary>
/// registration-attempt-history task 2 — proves the grant <c>GrantAndSeedRegistrationHistory</c> declares
/// actually lands on live SQL Server (the "insurance-pivot trap": a brand-new table gets NO grant
/// automatically, and SQLite-backed unit tests cannot catch a missing one). <c>merch.RegistrationAttempts</c>
/// is append-only (REQ-1.7's DB layer): SELECT + INSERT work, UPDATE/DELETE were never granted, so even a
/// bypass of the app-layer <c>AppendOnlyDescriptor</c> guard is denied here too.
/// Tagged Integration: the default unit run skips these; CI runs them against a live SQL service.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RegistrationAttemptGrantsTests
{
    private static Task InsertUserAsync(SqlConnection c, Guid userId, string subject) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT merch.Users
                (Id, Subject, Email, Status, CreatedAt, DisplayName, FirstName, LastName)
            VALUES (@id, @subject, N'attempt@example.com', 0, SYSUTCDATETIME(), N'First Last', N'First', N'Last');
            """,
            ("@id", userId), ("@subject", subject));

    private static Task InsertAttemptAsync(SqlConnection c, Guid attemptId, Guid userId, int attemptNo) =>
        IntegrationDb.ExecAsync(c,
            """
            INSERT merch.RegistrationAttempts
                (Id, MerchantUserId, AttemptNo, Purpose, FirstName, LastName, Email, SubmittedAt)
            VALUES (@id, @userId, @no, 0, N'First', N'Last', N'attempt@example.com', SYSUTCDATETIME());
            """,
            ("@id", attemptId), ("@userId", userId), ("@no", attemptNo));

    [Fact]
    public async Task PolApp_can_insert_and_read_attempts_and_the_write_survives_a_fresh_connection()
    {
        var userId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await using (var writer = await IntegrationDb.OpenAsync(IntegrationDb.AppConn))
        {
            await InsertUserAsync(writer, userId, $"attempt-grant-{userId:N}");
            await InsertAttemptAsync(writer, attemptId, userId, 1);
        }

        await using var reader = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        var count = Convert.ToInt32(await IntegrationDb.ScalarAsync(reader,
            "SELECT COUNT(*) FROM merch.RegistrationAttempts WHERE MerchantUserId = @userId", ("@userId", userId)));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PolApp_cannot_update_or_delete_the_append_only_attempt_table()
    {
        var userId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await using (var setup = await IntegrationDb.OpenAsync(IntegrationDb.AppConn))
        {
            await InsertUserAsync(setup, userId, $"attempt-deny-{userId:N}");
            await InsertAttemptAsync(setup, attemptId, userId, 1);
        }

        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(conn,
            "UPDATE merch.RegistrationAttempts SET FirstName = N'tampered' WHERE Id = @id;", ("@id", attemptId)));
        await Assert.ThrowsAsync<SqlException>(() => IntegrationDb.ExecAsync(conn,
            "DELETE FROM merch.RegistrationAttempts WHERE Id = @id;", ("@id", attemptId)));
    }

    [Fact]
    public async Task Duplicate_attempt_number_for_the_same_user_violates_the_unique_index()
    {
        var userId = Guid.NewGuid();

        await using var conn = await IntegrationDb.OpenAsync(IntegrationDb.AppConn);
        await InsertUserAsync(conn, userId, $"attempt-dup-{userId:N}");
        await InsertAttemptAsync(conn, Guid.NewGuid(), userId, 1);

        // REQ-1.9's DB decider: the losing racer's INSERT dies on IX_RegistrationAttempts_MerchantUserId_AttemptNo
        // (2601/2627), which MerchantUserUnitOfWork maps to a 409.
        var ex = await Assert.ThrowsAsync<SqlException>(() => InsertAttemptAsync(conn, Guid.NewGuid(), userId, 1));
        Assert.True(ex.Number is 2601 or 2627, $"expected unique violation, got {ex.Number}: {ex.Message}");
    }
}
