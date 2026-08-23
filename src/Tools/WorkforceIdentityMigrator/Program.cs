using System.Data;
using Admins.Domain.Users;
using Microsoft.Data.SqlClient;

namespace WorkforceIdentityMigrator;

internal static class Program
{
    public static Task<int> Main() => WorkforceIdentityMigration.RunAsync(
        Environment.GetEnvironmentVariable("POL_DESIGN_SQL"), Console.Out, CancellationToken.None);
}

public static class WorkforceIdentityMigration
{
    private const string MicrosoftProvider = "microsoft";
    private const string Converted = "converted";
    private const string NoOp = "no-op";

    public static async Task<int> RunAsync(
        string? connectionString, TextWriter output, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await output.WriteLineAsync("[workforce-identity] failed: configuration");
            return 2;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);

            await AcquireLockAsync(connection, transaction, cancellationToken);
            var state = await LoadStateAsync(connection, transaction, cancellationToken);
            var users = await LoadUsersAsync(connection, transaction, cancellationToken);
            var snapshots = await LoadSnapshotsAsync(connection, transaction, cancellationToken);

            ValidateProviderValues(users);
            var expectedKeys = ExpectedKeys(users);

            if (state.CompletedAt is not null)
            {
                VerifyCompleted(state, users, snapshots, expectedKeys);
                await transaction.CommitAsync(cancellationToken);
                await output.WriteLineAsync(
                    $"[workforce-identity] verified: snapshot={state.SnapshotCount} converted={state.ConvertedCount} no-op={state.NoOpCount}");
                return 0;
            }

            var plan = BuildPlan(state, users, snapshots, expectedKeys);
            await ApplyAsync(connection, transaction, users, expectedKeys, plan, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await output.WriteLineAsync(
                $"[workforce-identity] completed: snapshot={plan.Count} converted={plan.Count(x => x.Kind == Converted)} no-op={plan.Count(x => x.Kind == NoOp)}");
            return 0;
        }
        catch (Exception)
        {
            await output.WriteLineAsync("[workforce-identity] failed: invariant-or-database");
            return 1;
        }
    }

    private static async Task AcquireLockAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = N'admin-user-identity-mutation',
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (result < 0)
            throw new InvalidOperationException("Workforce identity migration lock failed.");
    }

    private static async Task<MigrationState> LoadStateAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<MigrationState>();
        await using var command = Command(connection, transaction,
            """
            SELECT Id, CompletedAt, SnapshotCount, ConvertedCount, NoOpCount
            FROM admin.WorkforceIdentityMigrations WITH (UPDLOCK, HOLDLOCK);
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new MigrationState(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));

        return rows is [{ Id: 1 }]
            ? rows[0]
            : throw new InvalidOperationException("Workforce identity migration state is invalid.");
    }

    private static async Task<IReadOnlyList<AdminRow>> LoadUsersAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<AdminRow>();
        await using var command = Command(connection, transaction,
            """
            SELECT Id, Provider, Subject, Email, WorkforceEmailKey
            FROM admin.Users WITH (UPDLOCK, HOLDLOCK)
            ORDER BY Id;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new AdminRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        return rows;
    }

    private static async Task<IReadOnlyList<SnapshotRow>> LoadSnapshotsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<SnapshotRow>();
        await using var command = Command(connection, transaction,
            """
            SELECT AdminUserId, LegacySubject, CanonicalSubject, ConversionKind
            FROM admin.WorkforceIdentitySubjectRollback WITH (UPDLOCK, HOLDLOCK)
            ORDER BY AdminUserId;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new SnapshotRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    private static void ValidateProviderValues(IReadOnlyList<AdminRow> users)
    {
        if (users.Any(user =>
                user.Subject is not null
                && string.Equals(user.Provider, MicrosoftProvider, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(user.Provider, MicrosoftProvider, StringComparison.Ordinal)))
            throw new InvalidOperationException("Microsoft provider discriminator is not canonical.");
    }

    private static IReadOnlyDictionary<Guid, string?> ExpectedKeys(IReadOnlyList<AdminRow> users)
    {
        var expected = users.ToDictionary(
            user => user.Id,
            user => WorkforceEmail.TryCanonicalize(user.Email, out var canonical) ? canonical : null);

        if (expected.Values.Where(value => value is not null)
            .GroupBy(value => value!, StringComparer.Ordinal)
            .Any(group => group.Count() > 1))
            throw new InvalidOperationException("Canonical workforce email ownership is ambiguous.");

        return expected;
    }

    private static IReadOnlyList<PlannedRow> BuildPlan(
        MigrationState state,
        IReadOnlyList<AdminRow> users,
        IReadOnlyList<SnapshotRow> snapshots,
        IReadOnlyDictionary<Guid, string?> expectedKeys)
    {
        var microsoft = users
            .Where(user => string.Equals(user.Provider, MicrosoftProvider, StringComparison.Ordinal)
                           && user.Subject is not null)
            .ToDictionary(user => user.Id);
        var snapshotById = snapshots.ToDictionary(snapshot => snapshot.AdminUserId);
        if (state.SnapshotCount != snapshots.Count
            || microsoft.Count != snapshots.Count
            || microsoft.Keys.Except(snapshotById.Keys).Any()
            || snapshotById.Keys.Except(microsoft.Keys).Any())
            throw new InvalidOperationException("Microsoft identity snapshot set drifted.");

        var plan = new List<PlannedRow>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            var user = microsoft[snapshot.AdminUserId];
            if (snapshot.LegacySubject is null
                || !string.Equals(user.Subject, snapshot.LegacySubject, StringComparison.Ordinal))
                throw new InvalidOperationException("Microsoft identity snapshot value drifted.");

            var canonical = expectedKeys[user.Id]
                ?? throw new InvalidOperationException("Microsoft identity has no canonical workforce email.");
            var kind = string.Equals(user.Subject, canonical, StringComparison.Ordinal)
                ? NoOp
                : Guid.TryParseExact(user.Subject, "D", out var legacyId) && legacyId != Guid.Empty
                    ? Converted
                    : throw new InvalidOperationException("Microsoft identity subject format is unsupported.");
            plan.Add(new PlannedRow(user.Id, snapshot.LegacySubject, canonical, kind));
        }

        if (plan.GroupBy(row => row.CanonicalSubject, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Microsoft canonical identity is ambiguous.");

        return plan;
    }

    private static void VerifyCompleted(
        MigrationState state,
        IReadOnlyList<AdminRow> users,
        IReadOnlyList<SnapshotRow> snapshots,
        IReadOnlyDictionary<Guid, string?> expectedKeys)
    {
        if (state.SnapshotCount != snapshots.Count
            || state.ConvertedCount != snapshots.Count(row => row.Kind == Converted)
            || state.NoOpCount != snapshots.Count(row => row.Kind == NoOp)
            || state.ConvertedCount + state.NoOpCount != state.SnapshotCount
            || snapshots.Any(row => row.LegacySubject is null
                                    || row.CanonicalSubject is null
                                    || row.Kind is not (Converted or NoOp)))
            throw new InvalidOperationException("Completed workforce identity manifest is invalid.");

        foreach (var user in users)
        {
            var expectedKey = expectedKeys[user.Id];
            if (!string.Equals(user.WorkforceEmailKey, expectedKey, StringComparison.Ordinal))
                throw new InvalidOperationException("Workforce email key drifted.");

            if (string.Equals(user.Provider, MicrosoftProvider, StringComparison.Ordinal)
                && user.Subject is not null
                && (expectedKey is null || !string.Equals(user.Subject, expectedKey, StringComparison.Ordinal)))
                throw new InvalidOperationException("Microsoft identity drifted.");
        }

        var usersById = users.ToDictionary(user => user.Id);
        if (snapshots.Any(snapshot =>
                !usersById.TryGetValue(snapshot.AdminUserId, out var user)
                || !string.Equals(user.Subject, snapshot.CanonicalSubject, StringComparison.Ordinal)))
            throw new InvalidOperationException("Completed workforce identity snapshot drifted.");
    }

    private static async Task ApplyAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<AdminRow> users,
        IReadOnlyDictionary<Guid, string?> expectedKeys,
        IReadOnlyList<PlannedRow> plan,
        CancellationToken cancellationToken)
    {
        foreach (var row in plan)
        {
            await using var command = Command(connection, transaction,
                """
                UPDATE admin.WorkforceIdentitySubjectRollback
                SET CanonicalSubject = @canonical, ConversionKind = @kind
                WHERE AdminUserId = @id;
                """);
            command.Parameters.Add("@canonical", SqlDbType.NVarChar, 254).Value = row.CanonicalSubject;
            command.Parameters.Add("@kind", SqlDbType.NVarChar, 16).Value = row.Kind;
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = row.AdminUserId;
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken));
        }

        foreach (var user in users)
        {
            await using var command = Command(connection, transaction,
                "UPDATE admin.Users SET WorkforceEmailKey = @key WHERE Id = @id;");
            command.Parameters.Add("@key", SqlDbType.NVarChar, 254).Value =
                (object?)expectedKeys[user.Id] ?? DBNull.Value;
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = user.Id;
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken));
        }

        foreach (var row in plan.Where(row => row.Kind == Converted))
        {
            await using var command = Command(connection, transaction,
                """
                UPDATE admin.Users
                SET Subject = @canonical
                WHERE Id = @id
                  AND Subject COLLATE Latin1_General_100_BIN2 = @legacy;
                """);
            command.Parameters.Add("@canonical", SqlDbType.NVarChar, 256).Value = row.CanonicalSubject;
            command.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = row.AdminUserId;
            command.Parameters.Add("@legacy", SqlDbType.NVarChar, 256).Value = row.LegacySubject;
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken));
        }

        await using var complete = Command(connection, transaction,
            """
            UPDATE admin.WorkforceIdentityMigrations
            SET CompletedAt = SYSUTCDATETIME(),
                ConvertedCount = @converted,
                NoOpCount = @noOp
            WHERE Id = 1 AND CompletedAt IS NULL AND SnapshotCount = @snapshot;
            """);
        complete.Parameters.Add("@converted", SqlDbType.Int).Value = plan.Count(row => row.Kind == Converted);
        complete.Parameters.Add("@noOp", SqlDbType.Int).Value = plan.Count(row => row.Kind == NoOp);
        complete.Parameters.Add("@snapshot", SqlDbType.Int).Value = plan.Count;
        RequireOne(await complete.ExecuteNonQueryAsync(cancellationToken));
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql) =>
        new(sql, connection, transaction);

    private static void RequireOne(int affected)
    {
        if (affected != 1)
            throw new InvalidOperationException("Workforce identity migration write count is invalid.");
    }

    private sealed record MigrationState(
        int Id, DateTime? CompletedAt, int SnapshotCount, int ConvertedCount, int NoOpCount);

    private sealed record AdminRow(
        Guid Id, string? Provider, string? Subject, string Email, string? WorkforceEmailKey);

    private sealed record SnapshotRow(
        Guid AdminUserId, string? LegacySubject, string? CanonicalSubject, string? Kind);

    private sealed record PlannedRow(
        Guid AdminUserId, string LegacySubject, string CanonicalSubject, string Kind);
}
