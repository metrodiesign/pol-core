using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Admins.Domain.Users;
using Microsoft.Data.SqlClient;

namespace WorkforceIdentityMigrator;

internal static class Program
{
    public static Task<int> Main() => WorkforceIdentityMigration.RunAsync(
        WorkforceIdentityMigrationConnection.Resolve(
            Environment.GetEnvironmentVariable("POL_DESIGN_SQL"),
            Environment.GetEnvironmentVariable("DB_SERVER"),
            Environment.GetEnvironmentVariable("DB_PORT"),
            Environment.GetEnvironmentVariable("DB_NAME"),
            Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD"),
            Environment.GetEnvironmentVariable("DB_CA_CERTIFICATE_FILE")),
        WorkforceIdentityMigrationInputs.FromEnvironment(),
        Console.Out,
        CancellationToken.None);
}

/// <summary>Builds the privileged tool connection inside the process so the migrate entrypoint can keep the
/// established schema-then-tool order without exposing a composed connection string through container config.</summary>
public static class WorkforceIdentityMigrationConnection
{
    public static string? Resolve(
        string? configured,
        string? server,
        string? port,
        string? database,
        string? password,
        string? serverCertificate)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        if (string.IsNullOrWhiteSpace(server)
            || string.IsNullOrWhiteSpace(database)
            || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var resolvedPort = string.IsNullOrWhiteSpace(port) ? 1433
            : int.TryParse(port, out var parsedPort) && parsedPort is > 0 and <= 65535
                ? parsedPort
                : 0;
        if (resolvedPort == 0)
            return null;

        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = $"{server.Trim()},{resolvedPort}",
                InitialCatalog = database.Trim(),
                UserID = "sa",
                Password = password,
                Encrypt = string.IsNullOrWhiteSpace(serverCertificate)
                    ? SqlConnectionEncryptOption.Mandatory
                    : SqlConnectionEncryptOption.Strict,
                TrustServerCertificate = false,
            };
            if (!string.IsNullOrWhiteSpace(serverCertificate))
            {
                builder["Server Certificate"] = serverCertificate.Trim();
                builder["Host Name In Certificate"] = server.Trim();
            }
            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

public sealed record WorkforceIdentityMigrationInputs(
    string? ManifestFile,
    string? ManifestSha256,
    string? ApprovedTarget,
    string? ApprovalEvidence,
    string? CorrelationId,
    string? TenantId)
{
    public static WorkforceIdentityMigrationInputs FromEnvironment() => new(
        Environment.GetEnvironmentVariable("WORKFORCE_IDENTITY_MANIFEST_FILE"),
        Environment.GetEnvironmentVariable("WORKFORCE_IDENTITY_MANIFEST_SHA256"),
        Environment.GetEnvironmentVariable("WORKFORCE_IDENTITY_TARGET"),
        Environment.GetEnvironmentVariable("WORKFORCE_IDENTITY_APPROVAL_EVIDENCE"),
        Environment.GetEnvironmentVariable("WORKFORCE_IDENTITY_CORRELATION_ID"),
        Environment.GetEnvironmentVariable("WORKFORCE_TENANT_ID"));
}

public static class WorkforceIdentityMigration
{
    private const int ManifestSizeLimit = 10 * 1024 * 1024;
    private const int EvidenceLengthLimit = 256;
    private const int CorrelationLengthLimit = 128;
    private const string MicrosoftProvider = "microsoft";
    private const string Converted = "converted";
    private const string NoOp = "no-op";
    private const string IdentityLockResource = "admin-user-identity-mutation";
    private const string TenantLockResource = "admin-workforce-tenant-binding";
    private static readonly Guid SystemActorId = Guid.Parse("a9000000-0000-4000-8000-000000000001");

    public static Task<int> RunAsync(
        string? connectionString, TextWriter output, CancellationToken cancellationToken) =>
        RunAsync(connectionString, WorkforceIdentityMigrationInputs.FromEnvironment(), output, cancellationToken);

    public static async Task<int> RunAsync(
        string? connectionString,
        WorkforceIdentityMigrationInputs inputs,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await output.WriteLineAsync("[workforce-identity] failed: configuration");
            return 2;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (await IsCompletedAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                var verified = await VerifyCompletedAsync(connection, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(
                    $"[workforce-identity] verified: snapshot={verified.SnapshotCount} mapped={verified.MappedCount} no-op={verified.NoOpCount}");
                return 0;
            }

            var preflightCount = await CountRequiredRowsAsync(connection, cancellationToken).ConfigureAwait(false);
            PreparedManifest? prepared = null;
            if (preflightCount > 0)
                prepared = await PrepareManifestAsync(connection, inputs, cancellationToken).ConfigureAwait(false);

            var completed = await MapFirstRunAsync(connection, inputs, prepared, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"[workforce-identity] completed: snapshot={completed.SnapshotCount} mapped={completed.MappedCount} no-op={completed.NoOpCount}");
            return 0;
        }
        catch (Exception)
        {
            await output.WriteLineAsync("[workforce-identity] failed: invariant-or-database");
            return 1;
        }
    }

    private static async Task<bool> IsCompletedAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT CompletedAt FROM admin.WorkforceTenantIdentityMigrations WHERE Id = 1;", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is not null and not DBNull;
    }

    private static async Task<int> CountRequiredRowsAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM admin.Users
            WHERE Provider COLLATE Latin1_General_100_BIN2 = N'microsoft'
               OR Subject IS NULL;
            """, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<PreparedManifest> PrepareManifestAsync(
        SqlConnection connection,
        WorkforceIdentityMigrationInputs inputs,
        CancellationToken cancellationToken)
    {
        _ = RequireBounded(inputs.ApprovalEvidence, EvidenceLengthLimit);
        var correlationId = RequireBounded(inputs.CorrelationId, CorrelationLengthLimit);
        var tenantId = ParseRequiredGuid(inputs.TenantId);
        await VerifyTargetAsync(connection, inputs.ApprovedTarget, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(inputs.ManifestFile)
            || string.IsNullOrWhiteSpace(inputs.ManifestSha256))
        {
            throw new InvalidOperationException("Required manifest input is missing.");
        }

        var path = inputs.ManifestFile;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > ManifestSizeLimit)
            throw new InvalidOperationException("Manifest size is invalid.");

        byte[] contents;
        await using (var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > ManifestSizeLimit)
                throw new InvalidOperationException("Manifest size is invalid.");
            contents = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(contents, cancellationToken).ConfigureAwait(false);
            if (stream.Position != stream.Length)
                throw new InvalidOperationException("Manifest changed while it was read.");
        }

        if (inputs.ManifestSha256.Length != 64)
            throw new InvalidOperationException("Manifest digest is invalid.");
        var expectedDigest = Convert.FromHexString(inputs.ManifestSha256);
        if (expectedDigest.Length != 32)
            throw new InvalidOperationException("Manifest digest is invalid.");

        var actualDigest = SHA256.HashData(contents);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            throw new InvalidOperationException("Manifest digest does not match.");

        var entries = ParseManifest(contents);
        if (entries.Any(entry => entry.TenantId != tenantId))
            throw new InvalidOperationException("Manifest tenant is invalid.");

        return new PreparedManifest(entries, tenantId, correlationId);
    }

    private static async Task VerifyTargetAsync(
        SqlConnection connection, string? approvedTarget, CancellationToken cancellationToken)
    {
        var required = RequireBounded(approvedTarget, 512);
        await using var command = new SqlCommand(
            "SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ServerName')), DB_NAME();", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            throw new InvalidOperationException("Database target is unavailable.");
        }

        var actualDatabase = reader.GetString(1);
        var actualTarget = $"{NormalizeServer(connection.DataSource)}/{actualDatabase}";
        if (!string.Equals(required, actualTarget, StringComparison.Ordinal)
            || !string.Equals(actualDatabase, connection.Database, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Database target does not match approval.");
        }
    }

    private static string NormalizeServer(string dataSource)
    {
        var value = dataSource.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)
            ? dataSource[4..]
            : dataSource;
        var separator = value.LastIndexOf(',');
        return separator < 0 ? value : string.Concat(value.AsSpan(0, separator), ":", value.AsSpan(separator + 1));
    }

    private static async Task<MigrationCounts> MapFirstRunAsync(
        SqlConnection connection,
        WorkforceIdentityMigrationInputs inputs,
        PreparedManifest? prepared,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireLockAsync(connection, transaction, IdentityLockResource, cancellationToken).ConfigureAwait(false);
            var historicalState = await LoadHistoricalStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var historicalSnapshots = await LoadHistoricalSnapshotsAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var users = await LoadUsersAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var state = await LoadTenantStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (state.CompletedAt is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return await VerifyCompletedAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            await CompleteOrVerifyHistoricalAsync(
                connection, transaction, historicalState, historicalSnapshots, users,
                requireCurrentCanonicalSubject: true, cancellationToken).ConfigureAwait(false);

            var required = users
                .Where(user => string.Equals(user.Provider, MicrosoftProvider, StringComparison.Ordinal)
                    || user.Subject is null)
                .OrderBy(user => user.Id)
                .ToArray();
            await CaptureSnapshotAsync(connection, transaction, state, required, cancellationToken).ConfigureAwait(false);

            await AcquireLockAsync(connection, transaction, TenantLockResource, cancellationToken).ConfigureAwait(false);
            Guid? tenantId = null;
            if (required.Length > 0)
            {
                if (prepared is null)
                    throw new InvalidOperationException("Required manifest input is missing.");
                if (prepared.Entries.Count > required.Length)
                    throw new InvalidOperationException("Manifest entry count is invalid.");
                tenantId = await EnsureTenantAsync(
                    connection, transaction, prepared.TenantId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                tenantId = await LoadOptionalTenantAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            var plan = BuildMappingPlan(required, prepared, tenantId);
            await ApplyMappingsAsync(connection, transaction, plan, prepared?.CorrelationId, cancellationToken)
                .ConfigureAwait(false);
            ValidateFinalUsers(users, plan, tenantId);
            await CompleteTenantStateAsync(connection, transaction, plan, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MigrationCounts(
                required.Length,
                plan.Count(row => row.Kind == MappingKind.Mapped),
                plan.Count(row => row.Kind == MappingKind.NoOp));
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<MigrationCounts> VerifyCompletedAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        try
        {
            await AcquireLockAsync(connection, transaction, IdentityLockResource, cancellationToken).ConfigureAwait(false);
            var historicalState = await LoadHistoricalStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var historicalSnapshots = await LoadHistoricalSnapshotsAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var users = await LoadUsersAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (historicalState.CompletedAt is null)
                throw new InvalidOperationException("Completed tenant identity has incomplete historical state.");
            await CompleteOrVerifyHistoricalAsync(
                connection, transaction, historicalState, historicalSnapshots, users,
                requireCurrentCanonicalSubject: false, cancellationToken).ConfigureAwait(false);

            var state = await LoadTenantStateAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (state.CompletedAt is null
                || state.SnapshotCount < 0
                || state.MappedCount < 0
                || state.NoOpCount < 0
                || state.MappedCount + state.NoOpCount != state.SnapshotCount)
            {
                throw new InvalidOperationException("Completed tenant identity state is invalid.");
            }

            var snapshotIds = await LoadTenantSnapshotAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            if (snapshotIds.Count != state.SnapshotCount)
                throw new InvalidOperationException("Tenant identity snapshot count is invalid.");

            var tenantId = await LoadOptionalTenantAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            ValidateCompletedUsers(users, snapshotIds, tenantId);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new MigrationCounts(state.SnapshotCount, state.MappedCount, state.NoOpCount);
        }
        catch
        {
            if (transaction.Connection is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task AcquireLockAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string resource,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction,
            """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = N'Exclusive',
                @LockOwner = N'Transaction',
                @LockTimeout = 15000;
            SELECT @result;
            """);
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value = resource;
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (result < 0)
            throw new InvalidOperationException("Workforce identity migration lock failed.");
    }

    private static async Task<HistoricalMigrationState> LoadHistoricalStateAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<HistoricalMigrationState>();
        await using var command = Command(connection, transaction,
            """
            SELECT Id, CompletedAt, SnapshotCount, ConvertedCount, NoOpCount
            FROM admin.WorkforceIdentityMigrations WITH (UPDLOCK, HOLDLOCK);
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new HistoricalMigrationState(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        return rows is [{ Id: 1 }]
            ? rows[0]
            : throw new InvalidOperationException("Historical identity state is invalid.");
    }

    private static async Task<List<HistoricalSnapshot>> LoadHistoricalSnapshotsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<HistoricalSnapshot>();
        await using var command = Command(connection, transaction,
            """
            SELECT AdminUserId, LegacySubject, CanonicalSubject, ConversionKind
            FROM admin.WorkforceIdentitySubjectRollback WITH (UPDLOCK, HOLDLOCK)
            ORDER BY AdminUserId;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new HistoricalSnapshot(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        return rows;
    }

    private static async Task<List<AdminRow>> LoadUsersAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<AdminRow>();
        await using var command = Command(connection, transaction,
            """
            SELECT Id, Provider, TenantId, Subject, Email, Version
            FROM admin.Users WITH (UPDLOCK, HOLDLOCK)
            ORDER BY Id;
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new AdminRow(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        return rows;
    }

    private static async Task CompleteOrVerifyHistoricalAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        HistoricalMigrationState state,
        IReadOnlyList<HistoricalSnapshot> snapshots,
        List<AdminRow> users,
        bool requireCurrentCanonicalSubject,
        CancellationToken cancellationToken)
    {
        if (state.SnapshotCount != snapshots.Count || state.SnapshotCount < 0)
            throw new InvalidOperationException("Historical identity snapshot count is invalid.");

        var usersById = users.ToDictionary(user => user.Id);
        if (state.CompletedAt is not null)
        {
            if (state.ConvertedCount != snapshots.Count(row => row.Kind == Converted)
                || state.NoOpCount != snapshots.Count(row => row.Kind == NoOp)
                || state.ConvertedCount + state.NoOpCount != state.SnapshotCount
                || snapshots.Any(row => row.LegacySubject is null
                    || row.CanonicalSubject is null
                    || row.Kind is not (Converted or NoOp)
                    || !usersById.TryGetValue(row.AdminUserId, out var user)
                    || requireCurrentCanonicalSubject
                       && !string.Equals(user.Subject, row.CanonicalSubject, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Completed historical identity state is invalid.");
            }
            return;
        }

        var currentMicrosoft = users
            .Where(user => string.Equals(user.Provider, MicrosoftProvider, StringComparison.Ordinal)
                && user.Subject is not null)
            .ToDictionary(user => user.Id);
        if (currentMicrosoft.Count != snapshots.Count
            || currentMicrosoft.Keys.Except(snapshots.Select(row => row.AdminUserId)).Any())
        {
            throw new InvalidOperationException("Pending historical identity snapshot set drifted.");
        }

        var converted = 0;
        var noOp = 0;
        foreach (var snapshot in snapshots)
        {
            if (snapshot.LegacySubject is null
                || !currentMicrosoft.TryGetValue(snapshot.AdminUserId, out var user)
                || !string.Equals(user.Subject, snapshot.LegacySubject, StringComparison.Ordinal)
                || !WorkforceEmail.TryCanonicalize(user.Email, out var canonical))
            {
                throw new InvalidOperationException("Pending historical identity row is invalid.");
            }

            var kind = string.Equals(user.Subject, canonical, StringComparison.Ordinal)
                ? NoOp
                : Guid.TryParseExact(user.Subject, "D", out var legacyObjectId) && legacyObjectId != Guid.Empty
                    ? Converted
                    : throw new InvalidOperationException("Pending historical subject is unsupported.");
            converted += kind == Converted ? 1 : 0;
            noOp += kind == NoOp ? 1 : 0;

            await using (var updateSnapshot = Command(connection, transaction,
                """
                UPDATE admin.WorkforceIdentitySubjectRollback
                SET CanonicalSubject = @canonical, ConversionKind = @kind
                WHERE AdminUserId = @id;
                """))
            {
                updateSnapshot.Parameters.Add("@canonical", SqlDbType.NVarChar, 254).Value = canonical;
                updateSnapshot.Parameters.Add("@kind", SqlDbType.NVarChar, 16).Value = kind;
                updateSnapshot.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = user.Id;
                RequireOne(await updateSnapshot.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }

            if (kind == Converted)
            {
                await using var updateUser = Command(connection, transaction,
                    "UPDATE admin.Users SET Subject = @canonical WHERE Id = @id;");
                updateUser.Parameters.Add("@canonical", SqlDbType.NVarChar, 256).Value = canonical;
                updateUser.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = user.Id;
                RequireOne(await updateUser.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
                user.Subject = canonical;
            }
        }

        await using var complete = Command(connection, transaction,
            """
            UPDATE admin.WorkforceIdentityMigrations
            SET CompletedAt = SYSUTCDATETIME(), ConvertedCount = @converted, NoOpCount = @noOp
            WHERE Id = 1 AND CompletedAt IS NULL AND SnapshotCount = @snapshot;
            """);
        complete.Parameters.Add("@converted", SqlDbType.Int).Value = converted;
        complete.Parameters.Add("@noOp", SqlDbType.Int).Value = noOp;
        complete.Parameters.Add("@snapshot", SqlDbType.Int).Value = snapshots.Count;
        RequireOne(await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<TenantMigrationState> LoadTenantStateAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<TenantMigrationState>();
        await using var command = Command(connection, transaction,
            """
            SELECT Id, CompletedAt, SnapshotCount, MappedCount, NoOpCount
            FROM admin.WorkforceTenantIdentityMigrations WITH (UPDLOCK, HOLDLOCK);
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new TenantMigrationState(
                reader.GetInt32(0),
                reader.IsDBNull(1) ? null : reader.GetDateTime(1),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        return rows is [{ Id: 1 }]
            ? rows[0]
            : throw new InvalidOperationException("Tenant identity state is invalid.");
    }

    private static async Task CaptureSnapshotAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        TenantMigrationState state,
        IReadOnlyCollection<AdminRow> required,
        CancellationToken cancellationToken)
    {
        var existing = await LoadTenantSnapshotAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (state.SnapshotCount != 0 || state.MappedCount != 0 || state.NoOpCount != 0 || existing.Count != 0)
            throw new InvalidOperationException("Incomplete tenant identity state is invalid.");

        foreach (var user in required)
        {
            await using var insert = Command(connection, transaction,
                "INSERT admin.WorkforceTenantIdentitySnapshot (AdminUserId) VALUES (@id);");
            insert.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = user.Id;
            RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }

        await using var update = Command(connection, transaction,
            """
            UPDATE admin.WorkforceTenantIdentityMigrations
            SET SnapshotCount = @snapshot
            WHERE Id = 1 AND CompletedAt IS NULL AND SnapshotCount = 0 AND MappedCount = 0 AND NoOpCount = 0;
            """);
        update.Parameters.Add("@snapshot", SqlDbType.Int).Value = required.Count;
        RequireOne(await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<List<Guid>> LoadTenantSnapshotAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var ids = new List<Guid>();
        await using var command = Command(connection, transaction,
            "SELECT AdminUserId FROM admin.WorkforceTenantIdentitySnapshot WITH (UPDLOCK, HOLDLOCK) ORDER BY AdminUserId;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            ids.Add(reader.GetGuid(0));
        return ids;
    }

    private static async Task<Guid> EnsureTenantAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid configuredTenantId,
        CancellationToken cancellationToken)
    {
        var existing = await LoadTenantRowsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (existing.Count == 0)
        {
            await using var insert = Command(connection, transaction,
                "INSERT admin.WorkforceTenantBindings (Id, TenantId) VALUES (1, @tenantId);");
            insert.Parameters.Add("@tenantId", SqlDbType.UniqueIdentifier).Value = configuredTenantId;
            RequireOne(await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            return configuredTenantId;
        }

        return existing is [{ Id: 1 } row] && row.TenantId == configuredTenantId
            ? configuredTenantId
            : throw new InvalidOperationException("Workforce tenant binding is invalid.");
    }

    private static async Task<Guid?> LoadOptionalTenantAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = await LoadTenantRowsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        return rows.Count switch
        {
            0 => null,
            1 when rows[0].Id == 1 && rows[0].TenantId != Guid.Empty => rows[0].TenantId,
            _ => throw new InvalidOperationException("Workforce tenant binding is invalid."),
        };
    }

    private static async Task<List<TenantRow>> LoadTenantRowsAsync(
        SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken)
    {
        var rows = new List<TenantRow>();
        await using var command = Command(connection, transaction,
            "SELECT Id, TenantId FROM admin.WorkforceTenantBindings WITH (UPDLOCK, HOLDLOCK);");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new TenantRow(reader.GetByte(0), reader.GetGuid(1)));
        return rows;
    }

    private static IReadOnlyList<MappingPlanRow> BuildMappingPlan(
        IReadOnlyCollection<AdminRow> required,
        PreparedManifest? prepared,
        Guid? tenantId)
    {
        if (required.Count == 0)
        {
            if (prepared is not null && prepared.Entries.Count != 0)
                throw new InvalidOperationException("Manifest coverage is invalid.");
            return [];
        }

        if (prepared is null || tenantId is null || tenantId != prepared.TenantId)
            throw new InvalidOperationException("Manifest tenant is invalid.");
        if (prepared.Entries.Count != required.Count
            || prepared.Entries.GroupBy(entry => entry.AdminId).Any(group => group.Count() != 1)
            || prepared.Entries.GroupBy(entry => (entry.TenantId, entry.ObjectId)).Any(group => group.Count() != 1))
        {
            throw new InvalidOperationException("Manifest coverage or uniqueness is invalid.");
        }

        var entries = prepared.Entries.ToDictionary(entry => entry.AdminId);
        var requiredIds = required.Select(user => user.Id).ToHashSet();
        if (entries.Keys.Except(requiredIds).Any() || requiredIds.Except(entries.Keys).Any())
            throw new InvalidOperationException("Manifest coverage is invalid.");

        var plan = new List<MappingPlanRow>(required.Count);
        foreach (var user in required)
        {
            var entry = entries[user.Id];
            var subject = entry.ObjectId.ToString("D");
            if (user.TenantId is not null)
            {
                if (!string.Equals(user.Provider, MicrosoftProvider, StringComparison.Ordinal)
                    || user.TenantId != entry.TenantId
                    || !string.Equals(user.Subject, subject, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Existing tenant identity conflicts with manifest.");
                }
                plan.Add(new MappingPlanRow(user, entry.TenantId, subject, MappingKind.NoOp));
                continue;
            }

            if (!string.Equals(user.Provider, MicrosoftProvider, StringComparison.Ordinal) && user.Subject is not null)
                throw new InvalidOperationException("Manifest target is not a migration state.");
            plan.Add(new MappingPlanRow(user, entry.TenantId, subject, MappingKind.Mapped));
        }
        return plan;
    }

    private static async Task ApplyMappingsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<MappingPlanRow> plan,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (plan.Any(row => row.Kind == MappingKind.Mapped) && correlationId is null)
            throw new InvalidOperationException("Approval correlation is missing.");

        var occurredAt = DateTime.UtcNow;
        foreach (var row in plan.Where(row => row.Kind == MappingKind.Mapped))
        {
            await using (var update = Command(connection, transaction,
                """
                UPDATE admin.Users
                SET Provider = N'microsoft', TenantId = @tenantId, Subject = @subject,
                    Version = Version + 1, UpdatedAt = @occurredAt
                WHERE Id = @id AND TenantId IS NULL;
                """))
            {
                update.Parameters.Add("@tenantId", SqlDbType.UniqueIdentifier).Value = row.TenantId;
                update.Parameters.Add("@subject", SqlDbType.NVarChar, 256).Value = row.Subject;
                update.Parameters.Add("@occurredAt", SqlDbType.DateTime2).Value = occurredAt;
                update.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = row.User.Id;
                RequireOne(await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
            }

            await using var audit = Command(connection, transaction,
                """
                INSERT admin.UserAudits
                    (Id, Action, ActorType, ActorId, TargetAdminId, MerchantId, TargetRoleId, CorrelationId, OccurredAt)
                VALUES
                    (@auditId, N'microsoft-email-bind', N'system', @actorId, @targetId, NULL, NULL, @correlationId, @occurredAt);
                """);
            audit.Parameters.Add("@auditId", SqlDbType.UniqueIdentifier).Value = Guid.NewGuid();
            audit.Parameters.Add("@actorId", SqlDbType.UniqueIdentifier).Value = SystemActorId;
            audit.Parameters.Add("@targetId", SqlDbType.UniqueIdentifier).Value = row.User.Id;
            audit.Parameters.Add("@correlationId", SqlDbType.NVarChar, CorrelationLengthLimit).Value = correlationId!;
            audit.Parameters.Add("@occurredAt", SqlDbType.DateTime2).Value = occurredAt;
            RequireOne(await audit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    private static void ValidateFinalUsers(
        IReadOnlyList<AdminRow> users,
        IReadOnlyList<MappingPlanRow> plan,
        Guid? tenantId)
    {
        var mapped = plan.ToDictionary(row => row.User.Id);
        foreach (var user in users)
        {
            if (mapped.TryGetValue(user.Id, out var row) && row.Kind == MappingKind.Mapped)
            {
                user.Provider = MicrosoftProvider;
                user.TenantId = row.TenantId;
                user.Subject = row.Subject;
                user.Version++;
            }
        }
        ValidateCompletedUsers(users, plan.Select(row => row.User.Id).ToArray(), tenantId);
    }

    private static void ValidateCompletedUsers(
        IReadOnlyList<AdminRow> users,
        IReadOnlyCollection<Guid> snapshotIds,
        Guid? tenantId)
    {
        var usersById = users.ToDictionary(user => user.Id);
        if (snapshotIds.Any(id =>
            !usersById.TryGetValue(id, out var user)
            || tenantId is null
            || !MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
                user.Provider, user.TenantId, user.Subject, tenantId.Value, out var state)
            || state != MicrosoftWorkforceIdentityState.BoundMicrosoft))
        {
            throw new InvalidOperationException("Tenant identity snapshot is invalid.");
        }

        foreach (var user in users)
        {
            if (tenantId is { } persistedTenant)
            {
                if (!MicrosoftWorkforceIdentityPolicy.TryClassifyFinal(
                    user.Provider, user.TenantId, user.Subject, persistedTenant, out _))
                    throw new InvalidOperationException("Persisted Admin identity state is invalid.");
            }
            else if (string.Equals(user.Provider, MicrosoftProvider, StringComparison.OrdinalIgnoreCase)
                || user.TenantId is not null
                || user.Subject is null)
            {
                throw new InvalidOperationException("Persisted Admin identity state requires a tenant binding.");
            }
        }
    }

    private static async Task CompleteTenantStateAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyCollection<MappingPlanRow> plan,
        CancellationToken cancellationToken)
    {
        var mapped = plan.Count(row => row.Kind == MappingKind.Mapped);
        var noOp = plan.Count(row => row.Kind == MappingKind.NoOp);
        await using var complete = Command(connection, transaction,
            """
            UPDATE admin.WorkforceTenantIdentityMigrations
            SET CompletedAt = SYSUTCDATETIME(), MappedCount = @mapped, NoOpCount = @noOp
            WHERE Id = 1 AND CompletedAt IS NULL AND SnapshotCount = @snapshot;
            """);
        complete.Parameters.Add("@mapped", SqlDbType.Int).Value = mapped;
        complete.Parameters.Add("@noOp", SqlDbType.Int).Value = noOp;
        complete.Parameters.Add("@snapshot", SqlDbType.Int).Value = plan.Count;
        RequireOne(await complete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false));
    }

    private static IReadOnlyList<ManifestEntry> ParseManifest(ReadOnlySpan<byte> contents)
    {
        using var document = JsonDocument.Parse(contents.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var root = document.RootElement;
        RequireExactProperties(root, "schemaVersion", "entries");
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number
            || !root.GetProperty("schemaVersion").TryGetInt32(out var version)
            || version != 1
            || root.GetProperty("entries").ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Manifest schema is invalid.");
        }

        var entries = new List<ManifestEntry>();
        foreach (var element in root.GetProperty("entries").EnumerateArray())
        {
            RequireExactProperties(element, "adminId", "tenantId", "objectId");
            entries.Add(new ManifestEntry(
                ParseJsonGuid(element, "adminId"),
                ParseJsonGuid(element, "tenantId"),
                ParseJsonGuid(element, "objectId")));
        }
        return entries;
    }

    private static void RequireExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Manifest object is invalid.");
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedSet.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidOperationException("Manifest property set is invalid.");
        }
        if (!seen.SetEquals(expectedSet))
            throw new InvalidOperationException("Manifest property set is incomplete.");
    }

    private static Guid ParseJsonGuid(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String
            || !Guid.TryParse(value.GetString(), out var parsed)
            || parsed == Guid.Empty)
        {
            throw new InvalidOperationException("Manifest GUID is invalid.");
        }
        return parsed;
    }

    private static Guid ParseRequiredGuid(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty
            ? parsed
            : throw new InvalidOperationException("Required GUID input is invalid.");

    private static string RequireBounded(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value != value.Trim()
            || value.Length > maximum)
        {
            throw new InvalidOperationException("Required approval input is invalid.");
        }
        return value;
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction transaction, string sql) =>
        new(sql, connection, transaction);

    private static void RequireOne(int affected)
    {
        if (affected != 1)
            throw new InvalidOperationException("Workforce identity migration write count is invalid.");
    }

    private sealed record PreparedManifest(
        IReadOnlyList<ManifestEntry> Entries,
        Guid TenantId,
        string CorrelationId);

    private sealed record ManifestEntry(Guid AdminId, Guid TenantId, Guid ObjectId);
    private sealed record HistoricalMigrationState(
        int Id, DateTime? CompletedAt, int SnapshotCount, int ConvertedCount, int NoOpCount);
    private sealed record HistoricalSnapshot(
        Guid AdminUserId, string? LegacySubject, string? CanonicalSubject, string? Kind);
    private sealed record TenantMigrationState(
        int Id, DateTime? CompletedAt, int SnapshotCount, int MappedCount, int NoOpCount);
    private sealed record TenantRow(byte Id, Guid TenantId);
    private sealed record MigrationCounts(int SnapshotCount, int MappedCount, int NoOpCount);
    private enum MappingKind { Mapped, NoOp }
    private sealed record MappingPlanRow(AdminRow User, Guid TenantId, string Subject, MappingKind Kind);

    private sealed class AdminRow(
        Guid id, string provider, Guid? tenantId, string? subject, string? email, long version)
    {
        public Guid Id { get; } = id;
        public string Provider { get; set; } = provider;
        public Guid? TenantId { get; set; } = tenantId;
        public string? Subject { get; set; } = subject;
        public string? Email { get; } = email;
        public long Version { get; set; } = version;
    }
}
