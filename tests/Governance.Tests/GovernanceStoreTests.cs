using Admins.Application.Users;
using BuildingBlocks.Application;
using Contracts;
using Governance.Application;
using Governance.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Governance;

namespace Governance.Tests;

public sealed class GovernanceStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new(new DateTime(2026, 8, 10, 4, 0, 0, DateTimeKind.Utc));
    private ControlPlaneDbContext _db = default!;
    private GovernanceStore _store = default!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _db = new ControlPlaneDbContext(
            new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options,
            new AllowAllWrites(), new NoopTelemetry());
        await _db.Database.EnsureCreatedAsync();
        var unitOfWork = new ControlPlaneUnitOfWork(_db, new NoopTelemetry());
        var locks = new GovernanceSqlLockManager(_db);
        _store = new GovernanceStore(
            _db, unitOfWork, _clock, new DisabledAuditAnchorStore(), locks,
            new GovernanceAuditAppender(_db, locks));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Inbound_request_and_decision_are_idempotent_and_audited()
    {
        var maker = Guid.NewGuid();
        var checker = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        var merchantId = Guid.NewGuid();
        var requested = new ApprovalRequested(
            Guid.NewGuid(), approvalId, "merchant", merchantId, "routing.activate", "settings.manage",
            maker, "routing-ruleset", "rules-7", "v7", "corr-1", _clock.UtcNow);

        await _store.ReceiveAsync(requested, default);
        await _store.ReceiveAsync(requested, default);

        Assert.Single(await _db.ApprovalRequests.ToListAsync());
        Assert.Single(await _db.ApprovalEvents.ToListAsync());
        Assert.Single(await _db.AuditRecords.ToListAsync());

        var access = Access(checker, merchantId);
        var intent = new DecisionIntent(
            approvalId, ApprovalDecision.Approve, "checked", 1, "v7", "idem-1", "corr-2", access);
        var decided = await _store.DecideAsync(intent, default);
        var replayed = await _store.DecideAsync(intent, default);

        Assert.False(decided.Replayed);
        Assert.True(replayed.Replayed);
        Assert.Equal("approved", replayed.Approval.Status);
        Assert.Equal(2, await _db.ApprovalEvents.CountAsync());
        Assert.Equal(2, await _db.AuditRecords.CountAsync());
        Assert.Single(await _db.GovernanceOutboxMessages.ToListAsync());
        Assert.Single(await _db.OperationRecords.ToListAsync());

        var mismatch = intent with { Reason = "different" };
        var error = await Assert.ThrowsAsync<ConflictException>(() => _store.DecideAsync(mismatch, default));
        Assert.Equal("idempotency_key_reused", error.Code);
    }

    [Fact]
    public async Task Maker_and_stale_checker_are_rejected_without_partial_state()
    {
        var maker = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        await _store.ReceiveAsync(NewPlatformRequest(approvalId, maker), default);

        var makerError = await Assert.ThrowsAsync<GovernanceAccessDeniedException>(() => _store.DecideAsync(
            new DecisionIntent(
                approvalId, ApprovalDecision.Approve, "self", 1, "v1", "maker-key", "corr", Access(maker)),
            default));
        Assert.Equal("maker_cannot_decide", makerError.Code);

        var stale = await Assert.ThrowsAsync<ConflictException>(() => _store.DecideAsync(
            new DecisionIntent(
                approvalId, ApprovalDecision.Approve, "stale", 1, "v2", "stale-key", "corr", Access(Guid.NewGuid())),
            default));
        Assert.Equal("target_version_changed", stale.Code);

        var approval = await _db.ApprovalRequests.SingleAsync();
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Empty(await _db.OperationRecords.ToListAsync());
        Assert.Empty(await _db.GovernanceOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task Duplicate_execution_report_is_one_effect_and_tamper_blocks_reads()
    {
        var maker = Guid.NewGuid();
        var checker = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        await _store.ReceiveAsync(NewPlatformRequest(approvalId, maker), default);
        await _store.DecideAsync(new DecisionIntent(
            approvalId, ApprovalDecision.Approve, "checked", 1, "v1", "key", "corr", Access(checker)), default);
        var report = new ApprovalExecutionReported(
            Guid.NewGuid(), approvalId, checker, true, false, "activated", "v2", "corr-3", _clock.UtcNow);

        await _store.ReceiveAsync(report, default);
        await _store.ReceiveAsync(report, default);

        Assert.Equal(3, await _db.ApprovalEvents.CountAsync());
        Assert.Equal(3, await _db.AuditRecords.CountAsync());
        var auditId = await _db.AuditRecords.OrderBy(x => x.Sequence).Select(x => x.Id).FirstAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE AuditRecords SET Changes = '{{\"tampered\":true}}' WHERE Id = {auditId}");
        _db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<AuditIntegrityException>(() =>
            _store.GetAuditAsync(auditId, Access(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task Signed_anchor_mismatch_blocks_otherwise_valid_chain()
    {
        await _store.ReceiveAsync(NewPlatformRequest(Guid.NewGuid(), Guid.NewGuid()), default);
        var auditId = await _db.AuditRecords.Select(x => x.Id).SingleAsync();
        var anchored = new Dictionary<string, AuditAnchorCheckpoint>(StringComparer.Ordinal)
        {
            ["platform"] = new(
                "platform", 1, new string('f', 64),
                new DateTime(2026, 8, 10, 5, 0, 0, DateTimeKind.Utc)),
        };
        var locks = new GovernanceSqlLockManager(_db);
        var anchoredStore = new GovernanceStore(
            _db,
            new ControlPlaneUnitOfWork(_db, new NoopTelemetry()),
            _clock,
            new StaticAnchorStore(anchored),
            locks,
            new GovernanceAuditAppender(_db, locks));

        await Assert.ThrowsAsync<AuditIntegrityException>(() =>
            anchoredStore.GetAuditAsync(auditId, Access(Guid.NewGuid()), default));
    }

    [Fact]
    public async Task Microsoft_pre_provision_audit_stages_safe_platform_record_and_extends_hash_chain()
    {
        await _store.ReceiveAsync(NewPlatformRequest(Guid.NewGuid(), Guid.NewGuid()), default);
        var first = await _db.AuditRecords.AsNoTracking().SingleAsync();
        var target = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var occurredAt = _clock.UtcNow.AddMinutes(1);
        var fingerprint = $"sha256:{new string('a', 64)}";
        var writer = new AdminIdentityAuditWriter(
            new GovernanceAuditAppender(_db, new GovernanceSqlLockManager(_db)));

        await writer.AppendMicrosoftPreProvisionAsync(new AdminIdentityAuditEntry(
            actor, target, "HR ticket 42", fingerprint, 2, "corr-identity", occurredAt), default);

        Assert.Equal(1, await _db.AuditRecords.CountAsync());
        var staged = Assert.Single(
            _db.ChangeTracker.Entries<AuditRecord>(), x => x.State == EntityState.Added).Entity;
        Assert.True(staged.HasValidHash());

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var records = await _db.AuditRecords.AsNoTracking().OrderBy(x => x.Sequence).ToListAsync();
        var identity = Assert.Single(records, x => x.Action == "admin.microsoft-identity.preprovisioned");
        Assert.Equal(2, identity.Sequence);
        Assert.Equal("platform", identity.ScopeKey);
        Assert.Null(identity.MerchantId);
        Assert.Equal(actor, identity.ActorId);
        Assert.Equal("admin", identity.ResourceType);
        Assert.Equal(target.ToString("D"), identity.ResourceId);
        Assert.Equal("succeeded", identity.Result);
        Assert.Equal("v2", identity.ResourceVersion);
        Assert.Equal("corr-identity", identity.CorrelationId);
        Assert.Equal(occurredAt, identity.OccurredAt);
        Assert.Equal(DateTimeKind.Unspecified, identity.OccurredAt.Kind);
        Assert.True(identity.HasValidHash());
        Assert.Equal(
            $"{{\"fingerprint\":\"{fingerprint}\",\"provider\":\"microsoft\",\"reason\":\"HR ticket 42\",\"subjectBoundAfter\":true,\"subjectBoundBefore\":false}}",
            identity.Changes);
        Assert.Equal(first.Hash, identity.PreviousHash);
        Assert.NotNull(await _store.GetAuditAsync(identity.Id, Access(Guid.NewGuid()), default));

        var anchors = new Dictionary<string, AuditAnchorCheckpoint>(StringComparer.Ordinal)
        {
            ["platform"] = new(
                "platform", identity.Sequence, Convert.ToHexString(identity.Hash).ToLowerInvariant(), occurredAt),
        };
        var locks = new GovernanceSqlLockManager(_db);
        var anchoredStore = new GovernanceStore(
            _db,
            new ControlPlaneUnitOfWork(_db, new NoopTelemetry()),
            _clock,
            new StaticAnchorStore(anchors),
            locks,
            new GovernanceAuditAppender(_db, locks));
        Assert.NotNull(await anchoredStore.GetAuditAsync(identity.Id, Access(Guid.NewGuid()), default));
    }

    private ApprovalRequested NewPlatformRequest(Guid approvalId, Guid maker) => new(
        Guid.NewGuid(), approvalId, "platform", null, "apikey.rotate", "settings.manage", maker,
        "api-client", "client-1", "v1", "corr", _clock.UtcNow);

    private static GovernanceAccess Access(Guid actorId, Guid? merchantId = null) => new(
        actorId,
        IsUnrestricted: merchantId is null,
        Merchants: merchantId is { } id ? new HashSet<Guid> { id } : new HashSet<Guid>(),
        Permissions: new HashSet<string>(StringComparer.Ordinal) { "settings.manage", "audit.view" });

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; set; } = now;
    }

    private sealed class AllowAllWrites : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class NoopTelemetry : ISecurityTelemetry
    {
        public void Emit(DenialEvent denialEvent) { }
    }

    private sealed class StaticAnchorStore(
        IReadOnlyDictionary<string, AuditAnchorCheckpoint> anchors) : IAuditAnchorStore
    {
        public bool IsEnabled => true;

        public Task<IReadOnlyDictionary<string, AuditAnchorCheckpoint>> ReadAllLatestAsync(
            CancellationToken cancellationToken) => Task.FromResult(anchors);

        public Task AppendAsync(AuditAnchorCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
