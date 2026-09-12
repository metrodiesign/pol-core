using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Application.Migration;

public enum LegacyHumanStatus
{
    Active = 1,
    Pending = 2,
    Rejected = 3,
}

public enum MigrationConflictReason
{
    MissingIdentityEvidence = 1,
    ConflictingIdentityEvidence = 2,
    DuplicateIdentitySource = 3,
    MissingMerchantEvidence = 4,
    InvalidPaymentReference = 5,
    PaymentSessionCollision = 6,
    InvalidMoney = 7,
    DuplicateCallback = 8,
    MissingRegistrationEvidence = 9,
}

public enum MigrationRehearsalStatus
{
    Passed = 1,
    Blocked = 2,
}

public enum MigrationMaintenanceState
{
    Open = 1,
    Paused = 2,
    Cutover = 3,
    RolledBack = 4,
}

public enum MigrationRecoveryStatus
{
    Pending = 1,
    Replayed = 2,
}

public readonly record struct LegacyKey
{
    public LegacyKey(string kind, string id)
    {
        Kind = Required(kind, nameof(kind), 64);
        Id = Required(id, nameof(id), 200);
    }

    public string Kind { get; }
    public string Id { get; }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var result = value.Trim();
        return result.Length <= maxLength
            ? result
            : throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
    }
}

/// <summary>Immutable provider identity evidence. Email and display name are intentionally absent.</summary>
public sealed record IdentityEvidence
{
    public IdentityEvidence(string provider, string tenantId, string externalUserId, string evidenceReference)
    {
        Provider = Required(provider, nameof(provider), 64);
        TenantId = Required(tenantId, nameof(tenantId), 128);
        ExternalUserId = Required(externalUserId, nameof(externalUserId), 256);
        EvidenceReference = Required(evidenceReference, nameof(evidenceReference), 256);
    }

    public string Provider { get; }
    public string TenantId { get; }
    public string ExternalUserId { get; }
    public string EvidenceReference { get; }

    private static string Required(string value, string parameter, int maxLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        var result = value.Trim();
        return result.Length <= maxLength
            ? result
            : throw new ArgumentException($"{parameter} exceeds {maxLength} characters.", parameter);
    }
}

/// <summary>Legacy input row. Email/display name are retained only as non-authoritative source fields.</summary>
public sealed record LegacyHuman(
    LegacyKey Key,
    LegacyHumanStatus Status,
    IdentityEvidence? IdentityEvidence,
    Guid MerchantId,
    Guid? SaleId,
    string? Email,
    string? DisplayName,
    string? PhoneNumber = null,
    string? SaleCode = null,
    Guid? BranchId = null);

public sealed record LegacyHumanSession(
    LegacyKey Key,
    string SessionId,
    DateTime IssuedAt);

public sealed record LegacyPaymentHistory(
    string EventId,
    string Status,
    DateTime OccurredAt,
    string? ProviderReference);

public sealed record LegacyPaymentSession(
    Guid Id,
    Guid OrderId,
    Guid MerchantId,
    string ProviderAccountReference,
    string Environment,
    string PspRequestReference,
    string? PspTransactionReference,
    decimal Amount,
    string Currency,
    string Status,
    IReadOnlyList<LegacyPaymentHistory> History,
    string SnapshotJson,
    Guid? ProviderAccountId = null,
    Guid? CredentialVersionId = null,
    int Provider = 1,
    long ConfigurationVersion = 1);

public sealed record LegacyCallback(
    string CallbackId,
    string ProviderReference,
    DateTime ReceivedAt,
    string Payload,
    long SourceSequence);

public sealed record LegacyMigrationInput(
    Guid RunId,
    string SourceSnapshotId,
    IReadOnlyList<LegacyHuman> Humans,
    IReadOnlyList<LegacyHumanSession> HumanSessions,
    IReadOnlyList<LegacyPaymentSession> PaymentSessions,
    IReadOnlyList<LegacyCallback> PendingCallbacks)
{
    public static LegacyMigrationInput Create(
        Guid runId,
        string sourceSnapshotId,
        IEnumerable<LegacyHuman> humans,
        IEnumerable<LegacyHumanSession> humanSessions,
        IEnumerable<LegacyPaymentSession> paymentSessions,
        IEnumerable<LegacyCallback>? pendingCallbacks = null)
    {
        if (runId == Guid.Empty)
            throw new ArgumentException("RunId is required.", nameof(runId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSnapshotId, nameof(sourceSnapshotId));
        return new(
            runId,
            sourceSnapshotId.Trim(),
            Materialize(humans, nameof(humans)),
            Materialize(humanSessions, nameof(humanSessions)),
            Materialize(paymentSessions, nameof(paymentSessions)),
            pendingCallbacks is null ? [] : Materialize(pendingCallbacks, nameof(pendingCallbacks)));
    }

    private static IReadOnlyList<T> Materialize<T>(IEnumerable<T> values, string parameter)
    {
        ArgumentNullException.ThrowIfNull(values, parameter);
        return values.ToArray();
    }
}

public sealed record LegacyIdentityMapEntry(
    Guid RunId,
    string LegacyKind,
    string LegacyId,
    Guid AccountId,
    Guid MerchantId,
    string EvidenceReference,
    DateTime MigratedAt);

public enum MigrationHumanTarget
{
    Account = 1,
    RegistrationAttempt = 2,
}

public sealed record MigratedHuman(
    LegacyKey Source,
    MigrationHumanTarget Target,
    Guid TargetId,
    Guid? AttemptId,
    Guid? AccountId,
    Guid MerchantId,
    IdentityEvidence? IdentityEvidence,
    bool RequiresFreshLogin,
    LegacyHumanStatus SourceStatus,
    string? Email,
    string? PhoneNumber,
    string? SaleCode,
    Guid? SaleId,
    Guid? BranchId,
    string? DisplayName);

public sealed record MigratedSession(
    LegacyKey Source,
    string SessionId,
    bool Revoked,
    bool RefreshTokenTransferred);

public sealed record MigrationSnapshot(
    int SchemaVersion,
    string SourceSnapshotId,
    string Provenance,
    Guid OrderId,
    Guid TransactionId,
    DateTime CapturedAt,
    string SafePayload);

public sealed record MigratedTransaction(
    Guid Id,
    Guid OrderId,
    Guid MerchantId,
    string ProviderAccountReference,
    string Environment,
    string PspRequestReference,
    string? PspTransactionReference,
    decimal Amount,
    string Currency,
    string Status,
    IReadOnlyList<LegacyPaymentHistory> History,
    MigrationSnapshot Snapshot,
    Guid? ProviderAccountId,
    Guid? CredentialVersionId,
    int Provider,
    long ConfigurationVersion);

public sealed record MigrationConflict(
    Guid Id,
    Guid RunId,
    string EntityKind,
    string EntityId,
    MigrationConflictReason Reason,
    string SafeDetails);

public sealed class MigrationConflictReport
{
    public MigrationConflictReport(IEnumerable<MigrationConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        Conflicts = new ReadOnlyCollection<MigrationConflict>(
            conflicts.OrderBy(x => x.EntityKind, StringComparer.Ordinal)
                .ThenBy(x => x.EntityId, StringComparer.Ordinal)
                .ThenBy(x => x.Reason)
                .ToArray());
        Fingerprint = FingerprintOf(Conflicts);
    }

    public IReadOnlyList<MigrationConflict> Conflicts { get; }
    public bool HasBlockingConflicts => Conflicts.Count > 0;
    public string Fingerprint { get; }

    private static string FingerprintOf(IEnumerable<MigrationConflict> conflicts)
    {
        var canonical = string.Join('\n', conflicts.Select(x =>
            $"{x.EntityKind}|{x.EntityId}|{x.Reason}|{x.SafeDetails}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

public sealed record MigrationInvariantReport(
    int SourceHumanCount,
    int SourcePaymentSessionCount,
    int MigratedTransactionCount,
    IReadOnlyDictionary<string, decimal> SourceAmountByCurrency,
    IReadOnlyDictionary<string, decimal> MigratedAmountByCurrency,
    bool OrderIdsPreserved,
    bool ProviderReferencesPreserved,
    bool HistoryPreserved,
    bool SnapshotProvenancePresent)
{
    public bool Passed =>
        SourcePaymentSessionCount == MigratedTransactionCount
        && DictionariesEqual(SourceAmountByCurrency, MigratedAmountByCurrency)
        && OrderIdsPreserved
        && ProviderReferencesPreserved
        && HistoryPreserved
        && SnapshotProvenancePresent;

    private static bool DictionariesEqual(
        IReadOnlyDictionary<string, decimal> left,
        IReadOnlyDictionary<string, decimal> right) =>
        left.Count == right.Count && left.All(pair =>
            right.TryGetValue(pair.Key, out var amount) && amount == pair.Value);
}

public sealed record MigrationRehearsalResult(
    Guid RunId,
    string SourceSnapshotId,
    MigrationRehearsalStatus Status,
    IReadOnlyList<LegacyIdentityMapEntry> IdentityMap,
    IReadOnlyList<MigratedHuman> Humans,
    IReadOnlyList<MigratedSession> Sessions,
    IReadOnlyList<MigratedTransaction> Transactions,
    MigrationConflictReport Conflicts,
    MigrationInvariantReport Invariants,
    int ExternalCallCount)
{
    public bool CutoverBlocked => Status == MigrationRehearsalStatus.Blocked;
}

/// <summary>Capture-only boundary used by rehearsal tests. It has no provider implementation.</summary>
public sealed class MigrationSideEffectCapture
{
    public int PspChargeCalls { get; private set; }
    public int EmailCalls { get; private set; }
    public int SmsCalls { get; private set; }
    public int BusinessEventCalls { get; private set; }
    public int TotalCalls => PspChargeCalls + EmailCalls + SmsCalls + BusinessEventCalls;

    public void RecordPspCharge() => PspChargeCalls++;
    public void RecordEmail() => EmailCalls++;
    public void RecordSms() => SmsCalls++;
    public void RecordBusinessEvent() => BusinessEventCalls++;
}

public sealed class MigrationReadinessBlockedException(MigrationConflictReport report)
    : InvalidOperationException("Migration rehearsal is blocked by unresolved conflicts.")
{
    public MigrationConflictReport Report { get; } = report;
}

public sealed class MigrationMaintenanceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public sealed class MigrationReadinessRunner
{
    private const string MigrationProvenance = "MIGRATION_BACKFILL";

    public MigrationRehearsalResult Rehearse(
        LegacyMigrationInput input,
        DateTime capturedAt,
        MigrationSideEffectCapture? sideEffects = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var conflicts = new List<MigrationConflict>();
        var identities = BuildIdentityMap(input, capturedAt, conflicts);
        var humans = BuildHumans(input, identities, conflicts);
        var sessions = input.HumanSessions
            .Select(session => new MigratedSession(session.Key, session.SessionId, Revoked: true,
                RefreshTokenTransferred: false))
            .ToArray();
        var transactions = BuildTransactions(input, capturedAt, conflicts);
        var report = new MigrationConflictReport(conflicts);
        var invariants = BuildInvariants(input, transactions);
        return new MigrationRehearsalResult(
            input.RunId,
            input.SourceSnapshotId,
            report.HasBlockingConflicts || !invariants.Passed
                ? MigrationRehearsalStatus.Blocked
                : MigrationRehearsalStatus.Passed,
            identities,
            humans,
            sessions,
            transactions,
            report,
            invariants,
            ExternalCallCount: sideEffects?.TotalCalls ?? 0);
    }

    public static void EnsureCutoverAllowed(MigrationRehearsalResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.CutoverBlocked)
            throw new MigrationReadinessBlockedException(result.Conflicts);
    }

    private static IReadOnlyList<LegacyIdentityMapEntry> BuildIdentityMap(
        LegacyMigrationInput input,
        DateTime migratedAt,
        ICollection<MigrationConflict> conflicts)
    {
        var entries = new List<LegacyIdentityMapEntry>();
        foreach (var group in input.Humans.GroupBy(x => x.Key))
        {
            var active = group.Where(x => x.Status == LegacyHumanStatus.Active).ToArray();
            if (active.Length == 0)
                continue;
            if (active.Length > 1)
            {
                var reason = active.Select(x => x.IdentityEvidence)
                    .Distinct()
                    .Count() > 1
                    ? MigrationConflictReason.ConflictingIdentityEvidence
                    : MigrationConflictReason.DuplicateIdentitySource;
                AddConflict(conflicts, input.RunId, group.Key, reason,
                    reason == MigrationConflictReason.ConflictingIdentityEvidence
                        ? "active source rows claim different provider identity evidence"
                        : "more than one active source row claims the same legacy identity");
                continue;
            }

            var human = active[0];
            if (human.IdentityEvidence is null)
            {
                AddConflict(conflicts, input.RunId, group.Key, MigrationConflictReason.MissingIdentityEvidence,
                    "active human has no provider identity evidence");
                continue;
            }
            if (human.MerchantId == Guid.Empty)
            {
                AddConflict(conflicts, input.RunId, group.Key, MigrationConflictReason.MissingMerchantEvidence,
                    "active human has no explicit target MerchantId");
                continue;
            }

            entries.Add(new(
                input.RunId,
                group.Key.Kind,
                group.Key.Id,
                StableId("account", group.Key),
                human.MerchantId,
                human.IdentityEvidence.EvidenceReference,
                migratedAt));
        }

        return entries;
    }

    private static IReadOnlyList<MigratedHuman> BuildHumans(
        LegacyMigrationInput input,
        IReadOnlyList<LegacyIdentityMapEntry> identities,
        ICollection<MigrationConflict> conflicts)
    {
        var identityByKey = identities.ToDictionary(x => new LegacyKey(x.LegacyKind, x.LegacyId));
        var results = new List<MigratedHuman>();
        foreach (var human in input.Humans)
        {
            if (human.MerchantId == Guid.Empty)
            {
                AddConflict(conflicts, input.RunId, human.Key, MigrationConflictReason.MissingMerchantEvidence,
                    "human has no explicit target MerchantId");
                continue;
            }
            if (human.Status == LegacyHumanStatus.Active)
            {
                if (!identityByKey.TryGetValue(human.Key, out var identity))
                    continue;
                results.Add(new(
                    human.Key,
                    MigrationHumanTarget.Account,
                    identity.AccountId,
                    AttemptId: null,
                    identity.AccountId,
                    identity.MerchantId,
                    human.IdentityEvidence,
                    RequiresFreshLogin: true,
                    human.Status,
                    human.Email,
                    human.PhoneNumber,
                    human.SaleCode,
                    human.SaleId,
                    human.BranchId,
                    human.DisplayName));
                continue;
            }

            if (string.IsNullOrWhiteSpace(human.Email)
                || string.IsNullOrWhiteSpace(human.PhoneNumber)
                || string.IsNullOrWhiteSpace(human.SaleCode)
                || human.SaleId is null
                || human.BranchId is null)
            {
                AddConflict(conflicts, input.RunId, human.Key, MigrationConflictReason.MissingRegistrationEvidence,
                    "pending or rejected human lacks explicit registration contact and Sale/Branch evidence");
                continue;
            }

            var registrationId = StableId("registration", human.Key);
            var attemptId = StableId($"registration-attempt-{human.Status}", human.Key);
            results.Add(new(
                human.Key,
                MigrationHumanTarget.RegistrationAttempt,
                registrationId,
                attemptId,
                AccountId: null,
                human.MerchantId,
                human.IdentityEvidence,
                RequiresFreshLogin: true,
                human.Status,
                human.Email,
                human.PhoneNumber,
                human.SaleCode,
                human.SaleId,
                human.BranchId,
                human.DisplayName));
        }

        return results;
    }

    private static IReadOnlyList<MigratedTransaction> BuildTransactions(
        LegacyMigrationInput input,
        DateTime capturedAt,
        ICollection<MigrationConflict> conflicts)
    {
        var results = new List<MigratedTransaction>();
        foreach (var group in input.PaymentSessions.GroupBy(x => x.Id))
        {
            if (group.Count() != 1)
            {
                AddConflict(conflicts, input.RunId, new LegacyKey("PaymentSession", group.Key.ToString("D")),
                    MigrationConflictReason.PaymentSessionCollision,
                    "more than one source row claims the same preserved transaction id");
                continue;
            }

            var session = group.Single();
            if (session.OrderId == Guid.Empty || session.MerchantId == Guid.Empty
                || string.IsNullOrWhiteSpace(session.ProviderAccountReference)
                || string.IsNullOrWhiteSpace(session.PspRequestReference))
            {
                AddConflict(conflicts, input.RunId,
                    new LegacyKey("PaymentSession", session.Id.ToString("D")),
                    MigrationConflictReason.InvalidPaymentReference,
                    "order, merchant, provider account and PSP request reference are required");
                continue;
            }
            if (!IsCurrency(session.Currency) || session.Amount < 0)
            {
                AddConflict(conflicts, input.RunId,
                    new LegacyKey("PaymentSession", session.Id.ToString("D")),
                    MigrationConflictReason.InvalidMoney,
                    "amount must be non-negative and currency must be a three-letter ISO code");
                continue;
            }

            var history = session.History?.ToArray() ?? [];
            var snapshot = new MigrationSnapshot(
                SchemaVersion: 1,
                input.SourceSnapshotId,
                MigrationProvenance,
                session.OrderId,
                session.Id,
                capturedAt,
                SafeSnapshot(session));
            results.Add(new(
                session.Id,
                session.OrderId,
                session.MerchantId,
                session.ProviderAccountReference.Trim(),
                session.Environment.Trim(),
                session.PspRequestReference.Trim(),
                NormalizeOptional(session.PspTransactionReference),
                session.Amount,
                session.Currency.Trim().ToUpperInvariant(),
                session.Status.Trim(),
                history,
                snapshot,
                session.ProviderAccountId,
                session.CredentialVersionId,
                session.Provider,
                session.ConfigurationVersion));
        }

        return results;
    }

    private static MigrationInvariantReport BuildInvariants(
        LegacyMigrationInput input,
        IReadOnlyList<MigratedTransaction> transactions)
    {
        var sourceAmounts = input.PaymentSessions
            .GroupBy(x => x.Currency.Trim().ToUpperInvariant(), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(item => item.Amount), StringComparer.Ordinal);
        var targetAmounts = transactions
            .GroupBy(x => x.Currency, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Sum(item => item.Amount), StringComparer.Ordinal);
        var sourceOrderIds = input.PaymentSessions.Select(x => x.OrderId).OrderBy(x => x).ToArray();
        var targetOrderIds = transactions.Select(x => x.OrderId).OrderBy(x => x).ToArray();
        var sourceRefs = input.PaymentSessions.Select(x => x.PspRequestReference).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var targetRefs = transactions.Select(x => x.PspRequestReference).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var sourceHistory = input.PaymentSessions.Sum(x => x.History?.Count ?? 0);
        var targetHistory = transactions.Sum(x => x.History.Count);
        return new(
            input.Humans.Count,
            input.PaymentSessions.Count,
            transactions.Count,
            sourceAmounts,
            targetAmounts,
            sourceOrderIds.SequenceEqual(targetOrderIds),
            sourceRefs.SequenceEqual(targetRefs, StringComparer.Ordinal),
            sourceHistory == targetHistory,
            transactions.All(x => x.Snapshot.Provenance == MigrationProvenance));
    }

    private static string SafeSnapshot(LegacyPaymentSession session) =>
        $"{{\"schemaVersion\":1,\"provenance\":\"MIGRATION_BACKFILL\",\"orderId\":\"{session.OrderId:D}\",\"transactionId\":\"{session.Id:D}\",\"amount\":\"{session.Amount:0.0000}\",\"currency\":\"{session.Currency.Trim().ToUpperInvariant()}\"}}";

    private static void AddConflict(
        ICollection<MigrationConflict> conflicts,
        Guid runId,
        LegacyKey key,
        MigrationConflictReason reason,
        string safeDetails)
    {
        conflicts.Add(new(
            StableId($"conflict-{runId:D}-{reason}", key),
            runId,
            key.Kind,
            key.Id,
            reason,
            safeDetails));
    }

    private static bool IsCurrency(string? currency) =>
        currency is not null
        && currency.Trim().Length == 3
        && currency.Trim().All(char.IsAsciiLetter);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static Guid StableId(string purpose, LegacyKey key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"pol-core/migration/{purpose}/{key.Kind}/{key.Id}"));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes.AsSpan(0, 16));
    }
}

public sealed record MigrationRecoveryInboxEntry(
    Guid RunId,
    long Sequence,
    string CallbackId,
    string ProviderReference,
    DateTime ReceivedAt,
    string Payload,
    MigrationRecoveryStatus Status,
    DateTime? ReplayedAt);

public sealed record MigrationPauseToken(Guid RunId, string WriterId, long Watermark);

public sealed class MigrationWriterLease : IDisposable
{
    private readonly MigrationMaintenanceCoordinator _owner;
    private bool _released;

    internal MigrationWriterLease(MigrationMaintenanceCoordinator owner, Guid runId, string writerId)
    {
        _owner = owner;
        RunId = runId;
        WriterId = writerId;
    }

    public Guid RunId { get; }
    public string WriterId { get; }

    public void Dispose()
    {
        if (_released)
            return;
        _released = true;
        _owner.Release(this);
    }
}

public sealed record ForwardRollbackInput(
    IReadOnlyDictionary<Guid, string> TargetResults,
    IReadOnlyList<string> TargetEvents,
    long CallbackWatermark);

public sealed record ForwardRollbackResult(
    bool TargetPreserved,
    bool BackupRestored,
    IReadOnlyDictionary<Guid, string> PreservedResults,
    IReadOnlyList<string> PreservedEvents,
    IReadOnlyList<MigrationRecoveryInboxEntry> CallbacksToReplay);

/// <summary>In-process coordination seam. Persistence is supplied by the Task 9 SQL store.</summary>
public sealed class MigrationMaintenanceCoordinator
{
    private readonly object _gate = new();
    private readonly List<MigrationRecoveryInboxEntry> _callbacks = [];
    private MigrationWriterLease? _writer;
    private MigrationMaintenanceState _state = MigrationMaintenanceState.Open;
    private long _sequence;

    public MigrationMaintenanceState State
    {
        get { lock (_gate) return _state; }
    }

    public MigrationWriterLease AcquireWriter(Guid runId, string writerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(writerId);
        lock (_gate)
        {
            if (_writer is not null)
                throw new MigrationMaintenanceException("maintenance_writer_active", "Another migration writer owns the maintenance window.");
            if (_state == MigrationMaintenanceState.RolledBack)
                throw new MigrationMaintenanceException("maintenance_rollback_complete", "The maintenance run has already rolled back.");
            _writer = new MigrationWriterLease(this, runId, writerId.Trim());
            return _writer;
        }
    }

    public MigrationPauseToken Pause(MigrationWriterLease lease)
    {
        lock (_gate)
        {
            EnsureOwner(lease);
            if (_state != MigrationMaintenanceState.Open)
                throw new MigrationMaintenanceException("maintenance_not_open", "The maintenance window is not open.");
            _state = MigrationMaintenanceState.Paused;
            return new(lease.RunId, lease.WriterId, _sequence);
        }
    }

    public MigrationRecoveryInboxEntry ReceiveCallback(MigrationPauseToken token, LegacyCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            EnsurePaused(token);
            if (_callbacks.Any(x => x.CallbackId == callback.CallbackId))
                return _callbacks.Single(x => x.CallbackId == callback.CallbackId);
            var entry = new MigrationRecoveryInboxEntry(
                token.RunId,
                ++_sequence,
                callback.CallbackId,
                callback.ProviderReference,
                callback.ReceivedAt,
                callback.Payload,
                MigrationRecoveryStatus.Pending,
                ReplayedAt: null);
            _callbacks.Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<MigrationRecoveryInboxEntry> ReplayAfterWatermark(
        MigrationPauseToken token,
        DateTime replayedAt)
    {
        lock (_gate)
        {
            EnsurePaused(token);
            var replay = _callbacks
                .Where(x => x.RunId == token.RunId && x.Sequence > token.Watermark
                    && x.Status == MigrationRecoveryStatus.Pending)
                .OrderBy(x => x.Sequence)
                .ToArray();
            for (var i = 0; i < _callbacks.Count; i++)
            {
                var callback = _callbacks[i];
                if (replay.Contains(callback))
                    _callbacks[i] = callback with { Status = MigrationRecoveryStatus.Replayed, ReplayedAt = replayedAt };
            }
            return replay.Select(x => x with { Status = MigrationRecoveryStatus.Replayed, ReplayedAt = replayedAt }).ToArray();
        }
    }

    public ForwardRollbackResult RollbackForward(
        MigrationPauseToken token,
        ForwardRollbackInput target,
        DateTime replayedAt)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            EnsurePaused(token);
            var replay = ReplayAfterWatermark(token, replayedAt);
            _state = MigrationMaintenanceState.RolledBack;
            return new(
                TargetPreserved: true,
                BackupRestored: false,
                new ReadOnlyDictionary<Guid, string>(target.TargetResults.ToDictionary(x => x.Key, x => x.Value)),
                target.TargetEvents.ToArray(),
                replay);
        }
    }

    public void CompleteCutover(MigrationPauseToken token)
    {
        lock (_gate)
        {
            EnsurePaused(token);
            _state = MigrationMaintenanceState.Cutover;
        }
    }

    internal void Release(MigrationWriterLease lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_writer, lease))
                _writer = null;
        }
    }

    private void EnsureOwner(MigrationWriterLease lease)
    {
        if (!ReferenceEquals(_writer, lease))
            throw new MigrationMaintenanceException("maintenance_writer_required", "The active migration writer is required.");
    }

    private void EnsurePaused(MigrationPauseToken token)
    {
        if (_state != MigrationMaintenanceState.Paused
            || _writer is null
            || _writer.RunId != token.RunId
            || !string.Equals(_writer.WriterId, token.WriterId, StringComparison.Ordinal))
        {
            throw new MigrationMaintenanceException("maintenance_pause_required", "Callbacks and recovery require the active paused writer.");
        }
    }
}
