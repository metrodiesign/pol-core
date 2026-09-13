using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Platform.Application.Migration;

namespace BuildingBlocks.Infrastructure.Persistence.MigrationReadiness;

/// <summary>Durable operator-only store for Task 9 rehearsal evidence. It is never called during host startup.</summary>
public sealed class SqlMigrationReadinessStore(DbConnection connection)
{
    private readonly DbConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task PersistAsync(MigrationRehearsalResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await OpenAsync(cancellationToken);
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(transaction, """
                INSERT INTO cfg.MigrationRuns
                    (RunId, SourceSnapshotId, Status, ConflictFingerprint, InvariantsJson, CapturedAt, ExternalCallCount)
                VALUES (@runId, @source, @status, @fingerprint, @invariants, @capturedAt, @externalCalls);
                """, cancellationToken,
                ("@runId", result.RunId),
                ("@source", result.SourceSnapshotId),
                ("@status", (int)result.Status),
                ("@fingerprint", result.Conflicts.Fingerprint),
                ("@invariants", JsonSerializer.Serialize(result.Invariants)),
                ("@capturedAt", result.Transactions.FirstOrDefault()?.Snapshot.CapturedAt ?? DateTime.UtcNow),
                ("@externalCalls", result.ExternalCallCount));

            foreach (var entry in result.IdentityMap)
            {
                await ExecuteAsync(transaction, """
                    INSERT INTO cfg.LegacyIdentityMaps
                        (RunId, LegacyKind, LegacyId, AccountId, MerchantId, EvidenceReference, MigratedAt)
                    VALUES (@runId, @kind, @legacyId, @accountId, @merchantId, @evidence, @migratedAt);
                    """, cancellationToken,
                    ("@runId", entry.RunId), ("@kind", entry.LegacyKind), ("@legacyId", entry.LegacyId),
                    ("@accountId", entry.AccountId), ("@merchantId", entry.MerchantId),
                    ("@evidence", entry.EvidenceReference),
                    ("@migratedAt", entry.MigratedAt));
            }

            foreach (var conflict in result.Conflicts.Conflicts)
            {
                await ExecuteAsync(transaction, """
                    INSERT INTO cfg.MigrationConflicts
                        (Id, RunId, EntityKind, EntityId, ReasonCode, SafeDetails, ResolutionStatus)
                    VALUES (@id, @runId, @kind, @entityId, @reason, @details, N'UNRESOLVED');
                    """, cancellationToken,
                    ("@id", conflict.Id), ("@runId", conflict.RunId), ("@kind", conflict.EntityKind),
                    ("@entityId", conflict.EntityId), ("@reason", (int)conflict.Reason),
                    ("@details", conflict.SafeDetails));
            }

            foreach (var migrated in result.Transactions)
            {
                await ExecuteAsync(transaction, """
                    INSERT INTO cfg.MigratedTransactions
                        (RunId, TransactionId, OrderId, MerchantId, ProviderAccountReference, Environment,
                         PspRequestReference, PspTransactionReference, Amount, Currency, Status, HistoryJson,
                         SnapshotJson, Provenance)
                    VALUES (@runId, @transactionId, @orderId, @merchantId, @providerAccount, @environment,
                            @requestReference, @transactionReference, @amount, @currency, @status, @history,
                            @snapshot, @provenance);
                    """, cancellationToken,
                    ("@runId", result.RunId), ("@transactionId", migrated.Id), ("@orderId", migrated.OrderId),
                    ("@merchantId", migrated.MerchantId), ("@providerAccount", migrated.ProviderAccountReference),
                    ("@environment", migrated.Environment), ("@requestReference", migrated.PspRequestReference),
                    ("@transactionReference", (object?)migrated.PspTransactionReference ?? DBNull.Value),
                    ("@amount", migrated.Amount), ("@currency", migrated.Currency), ("@status", migrated.Status),
                    ("@history", JsonSerializer.Serialize(migrated.History)),
                    ("@snapshot", migrated.Snapshot.SafePayload), ("@provenance", migrated.Snapshot.Provenance));
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task SaveRecoveryCallbackAsync(
        MigrationRecoveryInboxEntry entry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await OpenAsync(cancellationToken);
        try
        {
            await ExecuteAsync(null, """
                INSERT INTO cfg.MigrationRecoveryInbox
                    (RunId, Sequence, CallbackId, ProviderReference, ReceivedAt, Payload, Status, ReplayedAt)
                SELECT @runId, @sequence, @callbackId, @providerReference, @receivedAt, @payload, @status, @replayedAt
                WHERE NOT EXISTS
                    (SELECT 1 FROM cfg.MigrationRecoveryInbox WHERE RunId = @runId AND CallbackId = @callbackId);
                """, cancellationToken,
                ("@runId", entry.RunId), ("@sequence", entry.Sequence), ("@callbackId", entry.CallbackId),
                ("@providerReference", entry.ProviderReference), ("@receivedAt", entry.ReceivedAt),
                ("@payload", entry.Payload), ("@status", (int)entry.Status),
                ("@replayedAt", (object?)entry.ReplayedAt ?? DBNull.Value));
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601
            && ex.Message.Contains("UQ_MigrationRecoveryInbox_Run_Callback", StringComparison.Ordinal))
        {
            // A concurrent callback with the same (RunId, CallbackId) won the race between the
            // NOT EXISTS probe and the insert; the UQ_MigrationRecoveryInbox_Run_Callback unique
            // index is the backstop. Swallow ONLY that named constraint so the dedupe stays
            // idempotent under concurrency. 2627/2601 alone is ambiguous (see
            // ProvisioningCoordinator.IsDuplicateKeyViolation): a PK_MigrationRecoveryInbox
            // (RunId, Sequence) violation is a DIFFERENT callback colliding on a reused Sequence
            // and must surface loudly rather than be lost, so it is deliberately not caught here.
        }
    }

    /// <summary>Backfills the real target owners after the rehearsal report is green.</summary>
    public async Task BackfillTargetsAsync(
        MigrationRehearsalResult result,
        SqlMigrationWriterLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(lease);
        await lease.EnsureOwnedAsync(_connection, cancellationToken);
        MigrationReadinessRunner.EnsureCutoverAllowed(result);
        ValidateTargetEvidence(result);
        await OpenAsync(cancellationToken);
        var transaction = lease.Transaction;
        try
        {
            foreach (var human in result.Humans)
            {
                if (human.Target == MigrationHumanTarget.Account)
                {
                    await ExecuteAsync(transaction, """
                        INSERT INTO acct.Accounts
                            (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                        SELECT @id, 2, @displayName, 1, 0, @at, @at
                        WHERE NOT EXISTS (SELECT 1 FROM acct.Accounts WHERE Id = @id);
                        INSERT INTO acct.LoginAccounts
                            (Id, AccountId, Provider, TenantId, ExternalUserId, Email, DisplayName, LastLoginAt)
                        SELECT @loginId, @id, @provider, @tenant, @externalId, @email, @displayName, @at
                        WHERE NOT EXISTS (SELECT 1 FROM acct.LoginAccounts WHERE AccountId = @id);
                        """, cancellationToken,
                        ("@id", human.AccountId!.Value),
                        ("@loginId", MigrationReadinessRunner.StableId("login", human.Source)),
                        ("@provider", human.IdentityEvidence!.Provider),
                        ("@tenant", human.IdentityEvidence.TenantId),
                        ("@externalId", human.IdentityEvidence.ExternalUserId),
                        ("@email", (object?)human.Email ?? DBNull.Value),
                        ("@displayName", (object?)human.DisplayName ?? "Migrated account"),
                        ("@at", result.Transactions.FirstOrDefault()?.Snapshot.CapturedAt ?? DateTime.UtcNow));
                }
                else
                {
                    var registrationStatus = human.SourceStatus == LegacyHumanStatus.Rejected ? 4 : 2;
                    var attemptStatus = human.SourceStatus == LegacyHumanStatus.Rejected ? 3 : 1;
                    var attemptId = human.AttemptId!.Value;
                    await ExecuteAsync(transaction, """
                        INSERT INTO acct.AgentRegistrations
                            (Id, MerchantId, Provider, TenantId, ExternalUserId, CurrentAttemptId, CurrentAttemptNo,
                             Status, SaleCode, Email, PhoneNumber, ProfileJson, CreatedAt, UpdatedAt, Version)
                        SELECT @registrationId, @merchantId, @provider, @tenant, @externalId, @attemptId, 1,
                               @registrationStatus, @saleCode, @email, @phone, N'{}', @at, @at, 1
                        WHERE NOT EXISTS (SELECT 1 FROM acct.AgentRegistrations WHERE Id = @registrationId);
                        INSERT INTO acct.AgentRegistrationAttempts
                            (Id, RegistrationId, MerchantId, AttemptNo, Provider, TenantId, ExternalUserId, SaleCode,
                             SaleId, BranchId, SaleVersion, BranchVersion, Email, PhoneNumber, ProfileJson,
                             IdempotencyKey, IntentHash, Status, SubmittedAt, RejectionReason, ContactEvidenceReference,
                             Version)
                        SELECT @attemptId, @registrationId, @merchantId, 1, @provider, @tenant, @externalId, @saleCode,
                               @saleId, @branchId, 1, 1, @email, @phone, N'{}', @idempotency, @intentHash, @attemptStatus,
                               @at, @rejectionReason, @contactEvidence, 1
                        WHERE NOT EXISTS (SELECT 1 FROM acct.AgentRegistrationAttempts WHERE Id = @attemptId);
                        """, cancellationToken,
                        ("@registrationId", human.TargetId), ("@attemptId", attemptId),
                        ("@merchantId", human.MerchantId), ("@provider", human.IdentityEvidence?.Provider ?? "legacy"),
                        ("@tenant", human.IdentityEvidence?.TenantId ?? "legacy"),
                        ("@externalId", human.IdentityEvidence?.ExternalUserId ?? human.Source.Id),
                        ("@registrationStatus", registrationStatus), ("@attemptStatus", attemptStatus),
                        ("@saleCode", human.SaleCode!), ("@email", human.Email!), ("@phone", human.PhoneNumber!),
                        ("@saleId", human.SaleId!.Value), ("@branchId", human.BranchId!.Value),
                        ("@idempotency", $"migration:{result.RunId:N}:{attemptId:N}"),
                        ("@intentHash", Hash($"{result.RunId:N}|{human.Source.Kind}|{human.Source.Id}")),
                        ("@at", result.Transactions.FirstOrDefault()?.Snapshot.CapturedAt ?? DateTime.UtcNow),
                        ("@rejectionReason", human.SourceStatus == LegacyHumanStatus.Rejected ? "migrated_rejected" : DBNull.Value),
                        ("@contactEvidence", (object?)human.IdentityEvidence?.EvidenceReference ?? DBNull.Value));
                }
            }

            foreach (var migrated in result.Transactions)
            {
                var status = ToTransactionStatus(migrated.Status);
                await ExecuteAsync(transaction, """
                    INSERT INTO txn.Transactions
                        (Id, MerchantId, OrderId, TransactionNo, AttemptNo, PaymentMethod, Provider,
                         ProviderAccountId, Environment, CredentialVersionId, ConfigurationVersion,
                         ProviderRequestReference, ProviderReference, Status, ProviderStatus, OrderSnapshot,
                         NeedsReview, CreatedAt, UpdatedAt, InquiryAttempts, Version, AmountAmount, AmountCurrency)
                    SELECT @id, @merchantId, @orderId, @transactionNo, 1, N'card', @provider,
                           @providerAccountId, @environment, @credentialVersionId, @configurationVersion,
                           @requestReference, @providerReference, @status, @providerStatus, @snapshot,
                           0, @at, @at, 0, 1, @amount, @currency
                    WHERE NOT EXISTS (SELECT 1 FROM txn.Transactions WHERE Id = @id);
                    UPDATE shop.Orders
                    SET PaymentSessionId = COALESCE(PaymentSessionId, @id),
                        PaymentStatus = CASE WHEN @status = 3 THEN 3 WHEN PaymentStatus = 3 THEN 3
                            WHEN @status IN (4, 5, 6) THEN 1 ELSE 2 END,
                        SuccessfulTransactionId = CASE
                            WHEN @status = 3 AND SuccessfulTransactionId IS NULL THEN @id
                            ELSE SuccessfulTransactionId END
                    WHERE Id = @orderId AND MerchantId = @merchantId;
                    """, cancellationToken,
                    ("@id", migrated.Id), ("@merchantId", migrated.MerchantId), ("@orderId", migrated.OrderId),
                    ("@transactionNo", $"MIG-{migrated.Id:N}"), ("@provider", migrated.Provider),
                    ("@providerAccountId", migrated.ProviderAccountId!.Value),
                    ("@environment", ToEnvironment(migrated.Environment)),
                    ("@credentialVersionId", migrated.CredentialVersionId!.Value),
                    ("@configurationVersion", migrated.ConfigurationVersion),
                    ("@requestReference", migrated.PspRequestReference),
                    ("@providerReference", (object?)migrated.PspTransactionReference ?? DBNull.Value),
                    ("@status", status), ("@providerStatus", migrated.Status),
                    ("@snapshot", migrated.Snapshot.SafePayload), ("@at", migrated.Snapshot.CapturedAt),
                    ("@amount", migrated.Amount), ("@currency", migrated.Currency));

                foreach (var history in migrated.History)
                {
                    await ExecuteAsync(transaction, """
                        INSERT INTO txn.TransactionEvents
                            (Id, MerchantId, TransactionId, Source, EventReference, Status, ProviderStatus,
                             EvidenceCode, SafeDetails, OccurredAt, ReceivedAt)
                        SELECT @id, @merchantId, @transactionId, N'migration', @eventReference, @status,
                               @providerStatus, N'MIGRATED_HISTORY', N'legacy_history', @occurredAt, @receivedAt
                        WHERE NOT EXISTS (SELECT 1 FROM txn.TransactionEvents WHERE Id = @id);
                        """, cancellationToken,
                        ("@id", MigrationReadinessRunner.StableId("transaction-event",
                            new LegacyKey(migrated.Id.ToString("D"), history.EventId))),
                        ("@merchantId", migrated.MerchantId), ("@transactionId", migrated.Id),
                        ("@eventReference", history.EventId), ("@status", ToTransactionStatus(history.Status!)),
                        ("@providerStatus", (object?)history.Status ?? DBNull.Value),
                        ("@occurredAt", history.OccurredAt), ("@receivedAt", migrated.Snapshot.CapturedAt));
                }
            }

            await lease.CommitAsync(cancellationToken);
        }
        catch
        {
            await lease.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<MigrationRecoveryInboxEntry>> ReplayPendingAsync(
        Guid runId,
        long watermark,
        DateTime replayedAt,
        CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken);
        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var entries = await ReadEntriesAsync(transaction, runId, watermark, cancellationToken);
            if (entries.Count > 0)
            {
                // Stamp ONLY the rows this transaction locked and read. A broad
                // "Sequence > @watermark" predicate would also mark any pending row that another
                // connection committed after the SELECT (SaveRecoveryCallbackAsync autocommits
                // without the lease), losing a callback that was never returned to the caller.
                var sequences = entries.Select(x => x.Sequence).ToArray();
                var placeholders = string.Join(", ", sequences.Select((_, i) => $"@seq{i}"));
                var parameters = new List<(string, object)>
                {
                    ("@replayed", (int)MigrationRecoveryStatus.Replayed),
                    ("@replayedAt", replayedAt),
                    ("@runId", runId),
                    ("@pending", (int)MigrationRecoveryStatus.Pending),
                };
                for (var i = 0; i < sequences.Length; i++)
                    parameters.Add(($"@seq{i}", sequences[i]));
                await ExecuteAsync(transaction, $"""
                    UPDATE cfg.MigrationRecoveryInbox
                    SET Status = @replayed, ReplayedAt = @replayedAt
                    WHERE RunId = @runId AND Status = @pending AND Sequence IN ({placeholders});
                    """, cancellationToken, parameters.ToArray());
            }
            await transaction.CommitAsync(cancellationToken);
            return entries.Select(x => x with
            {
                Status = MigrationRecoveryStatus.Replayed,
                ReplayedAt = replayedAt,
            }).ToArray();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<List<MigrationRecoveryInboxEntry>> ReadEntriesAsync(
        DbTransaction transaction,
        Guid runId,
        long watermark,
        CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        // ponytail: Sequence IN (...) bounded by 2100 SQL parameters; batch with TOP if replay backlog grows
        command.CommandText = """
            SELECT Sequence, CallbackId, ProviderReference, ReceivedAt, Payload, Status, ReplayedAt
            FROM cfg.MigrationRecoveryInbox WITH (UPDLOCK, READPAST)
            WHERE RunId = @runId AND Sequence > @watermark AND Status = @pending
            ORDER BY Sequence;
            """;
        Add(command, "@runId", runId);
        Add(command, "@watermark", watermark);
        Add(command, "@pending", (int)MigrationRecoveryStatus.Pending);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var results = new List<MigrationRecoveryInboxEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new(
                runId,
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTime(3),
                reader.GetString(4),
                (MigrationRecoveryStatus)reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetDateTime(6)));
        }
        return results;
    }

    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_connection.State != ConnectionState.Open)
            await _connection.OpenAsync(cancellationToken);
    }

    private async Task<int> ExecuteAsync(
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            Add(command, name, value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void ValidateTargetEvidence(MigrationRehearsalResult result)
    {
        foreach (var migrated in result.Transactions)
        {
            if (migrated.ProviderAccountId is null || migrated.CredentialVersionId is null)
                throw new InvalidOperationException($"Transaction {migrated.Id:D} lacks explicit provider account/credential evidence.");
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static int ToEnvironment(string value) => value.Trim().ToLowerInvariant() switch
    {
        "sandbox" => 1,
        "live" => 2,
        _ => throw new InvalidOperationException("Migration target environment evidence is invalid."),
    };

    private static int ToTransactionStatus(string value) => value.Trim().ToUpperInvariant() switch
    {
        "CREATED" => 1,
        "PENDING" or "PENDING_CONFIRMATION" => 2,
        "SUCCEEDED" or "PAID" => 3,
        "FAILED" => 4,
        "CANCELLED" or "CANCELED" => 5,
        "EXPIRED" => 6,
        _ => throw new InvalidOperationException("Migration transaction status evidence is invalid."),
    };
}
