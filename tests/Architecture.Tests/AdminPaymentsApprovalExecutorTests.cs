using System.Security.Cryptography;
using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using BuildingBlocks.Infrastructure.Vault;
using Contracts;
using Merchants.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using Persistence.MerchantRuntime.Vault;
using SharedKernel;

namespace Architecture.Tests;

public sealed class AdminPaymentsApprovalExecutorTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("e1000000-0000-4000-8000-000000000001");
    private static readonly Guid CheckerId = Guid.Parse("e2000000-0000-4000-8000-000000000001");
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public AdminPaymentsApprovalExecutorTests()
    {
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    [Fact]
    public async Task Same_event_and_different_event_for_same_approval_execute_once()
    {
        var (rulesetId, approvalId, version) = await SeedPendingRulesetAsync();
        await using var db = NewContext();
        var executor = Executor(db, NoOpSecurityTelemetry.Instance);
        var decision = Decision(Guid.NewGuid(), approvalId, rulesetId, version);

        await executor.ExecuteAsync(decision, default);
        await executor.ExecuteAsync(decision, default);
        await executor.ExecuteAsync(decision with { EventId = Guid.NewGuid() }, default);

        var execution = Assert.Single(await db.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal(ApprovalExecutionState.Succeeded, execution.State);
        Assert.Equal("routing_activated", execution.Outcome);
        var ruleset = await db.RoutingRulesets.SingleAsync(x => x.Id == rulesetId);
        Assert.Equal(RoutingRulesetStatus.Active, ruleset.Status);
        Assert.Equal(version + 1, ruleset.Version);

        var outbox = Assert.Single(await db.OutboxMessages
            .Where(x => x.Type == ApprovalExecutionReported.EventType).ToListAsync());
        var report = JsonSerializer.Deserialize<ApprovalExecutionReported>(
            outbox.Payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(report);
        Assert.Equal(CheckerId, report.ExecutorId);
        Assert.Equal(MerchantId, report.MerchantId);
        Assert.Equal("routing-ruleset", report.TargetType);
        Assert.Equal(rulesetId.ToString("D"), report.TargetId);
        Assert.Equal("routing_activated", report.Outcome);
        Assert.Equal("corr-approval", report.CorrelationId);
        Assert.Equal(Now, report.OccurredAt);
    }

    [Fact]
    public async Task Failed_execution_rolls_back_claim_and_emits_sanitized_external_telemetry()
    {
        var (rulesetId, approvalId, version) = await SeedPendingRulesetAsync();
        await using var db = NewContext();
        var telemetry = new CaptureTelemetry();
        var badDecision = Decision(Guid.NewGuid(), approvalId, rulesetId, version + 1);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Executor(db, telemetry).ExecuteAsync(badDecision, default));

        await using var verify = NewContext();
        Assert.Empty(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Empty(await verify.OutboxMessages
            .Where(x => x.Type == ApprovalExecutionReported.EventType).ToListAsync());
        var ruleset = await verify.RoutingRulesets.SingleAsync(x => x.Id == rulesetId);
        Assert.Equal(RoutingRulesetStatus.PendingApproval, ruleset.Status);
        Assert.Equal(version, ruleset.Version);
        var denial = Assert.Single(telemetry.Events);
        Assert.Equal(CheckerId, denial.ActorId);
        Assert.Equal(MerchantId, denial.TargetMerchant);
        Assert.Equal("corr-approval", denial.CorrelationId);
        Assert.Equal("Approval execution was denied or rolled back.", denial.Reason);
    }

    [Fact]
    public void Execution_record_model_is_tenant_scoped_and_unique_per_approval()
    {
        using var db = NewContext();
        var entity = db.Model.FindEntityType(typeof(ApprovalExecutionRecord));
        Assert.NotNull(entity);
        Assert.Equal("ApprovalExecutionRecords", entity.GetTableName());
        Assert.Equal("txn", entity.GetSchema());
        Assert.NotEmpty(entity.GetDeclaredQueryFilters());
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(x => x.Name).SequenceEqual([nameof(ApprovalExecutionRecord.ApprovalId)]));
    }

    [Fact]
    public async Task Approving_a_credential_change_retires_the_active_version_and_keeps_it_readable()
    {
        var seed = await SeedPendingCredentialAsync(candidateExpiresAt: Now.AddHours(24));
        await using var db = NewContext();
        var executor = CredentialExecutor(db, probeSucceeds: true);

        await executor.ExecuteAsync(CredentialDecision(seed.ApprovalId, seed.ConnectionId, seed.Version), default);

        await using var verify = NewContext();
        var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionId);
        Assert.Equal(seed.CandidateVersionId, connection.ActiveSecretVersionId);   // AC-6.3 candidate activated
        Assert.Null(connection.PendingApprovalId);
        Assert.Equal(PspConnectionHealth.Unknown, connection.Health);              // AC-6.3 active health reset
        var previous = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.ActiveVersionId);
        var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.CandidateVersionId);
        Assert.Equal(VaultSecretVersionState.Retired, previous.State);             // AC-6.3 retired, not deleted
        Assert.Null(previous.ExpiresAt);
        Assert.Equal(VaultSecretVersionState.Active, candidate.State);
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("psp_credentials_activated", execution.Outcome);

        // AC-6.5: a Session that pinned the retired version can still read it for webhook verification.
        var readback = await CredentialVault(verify).ReadVersionForServerAsync(MerchantId, seed.ActiveVersionId, default);
        Assert.Contains("active-secret", readback);
    }

    [Fact]
    public async Task Rejecting_a_credential_change_discards_the_candidate_and_keeps_the_active_version()
    {
        var seed = await SeedPendingCredentialAsync(candidateExpiresAt: Now.AddHours(24));
        await using var db = NewContext();
        var executor = CredentialExecutor(db, probeSucceeds: true);
        var rejected = CredentialDecision(seed.ApprovalId, seed.ConnectionId, seed.Version) with { Decision = "rejected" };

        await executor.ExecuteAsync(rejected, default);

        await using var verify = NewContext();
        var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionId);
        Assert.Equal(seed.ActiveVersionId, connection.ActiveSecretVersionId);      // AC-6.4 active kept
        Assert.Null(connection.PendingApprovalId);                                 // pending cleared
        Assert.Null(connection.PendingSecretVersionId);
        var previous = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.ActiveVersionId);
        var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.CandidateVersionId);
        Assert.Equal(VaultSecretVersionState.Active, previous.State);
        Assert.Equal(VaultSecretVersionState.Discarded, candidate.State);          // candidate discarded
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("psp_credentials_rejected", execution.Outcome);
    }

    [Fact]
    public async Task Approving_an_expired_candidate_discards_it_and_reports_credential_candidate_expired()
    {
        var seed = await SeedPendingCredentialAsync(candidateExpiresAt: Now.AddHours(-1));   // AC-6.6: past its 24h window
        await using var db = NewContext();
        var executor = CredentialExecutor(db, probeSucceeds: true);

        await executor.ExecuteAsync(CredentialDecision(seed.ApprovalId, seed.ConnectionId, seed.Version), default);

        await using var verify = NewContext();
        var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionId);
        Assert.Equal(seed.ActiveVersionId, connection.ActiveSecretVersionId);      // active untouched
        Assert.Null(connection.PendingApprovalId);                                 // pending cleared
        var previous = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.ActiveVersionId);
        var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.CandidateVersionId);
        Assert.Equal(VaultSecretVersionState.Active, previous.State);
        Assert.Equal(VaultSecretVersionState.Discarded, candidate.State);          // candidate discarded
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("credential_candidate_expired", execution.Outcome);
    }

    private async Task<(Guid ConnectionId, Guid ApprovalId, long Version, Guid ActiveVersionId, Guid CandidateVersionId)>
        SeedPendingCredentialAsync(DateTime candidateExpiresAt)
    {
        const string secretName = "psp-connection-credential";
        await using var db = NewContext();
        var vault = CredentialVault(db);
        var connection = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, secretName, Now);
        db.Merchants.Add(Merchant.CreateWithId(
            MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [PaymentMethods.Card], "{}", Now));
        db.PspConnections.Add(connection);
        await db.SaveChangesAsync();

        var active = await vault.StageVersionAsync(
            MerchantId, secretName, "{\"secretKey\":\"active-secret\"}", "{\"secretKey\":\"****tive\"}", Now.AddHours(24), default);
        await db.SaveChangesAsync();
        await vault.ActivateVersionAsync(MerchantId, active, default);
        connection.SetInitialSecretVersion(active, PspEnvironment.Sandbox);
        connection.RecordTest(true, "authenticated", Now);   // prove the activation resets a Healthy state
        await db.SaveChangesAsync();

        var candidate = await vault.StageVersionAsync(
            MerchantId, secretName, "{\"secretKey\":\"candidate-secret\"}", "{\"secretKey\":\"****date\"}", candidateExpiresAt, default);
        var approvalId = Guid.NewGuid();
        connection.StageSecretVersion(candidate, approvalId, PspEnvironment.Sandbox);
        await db.SaveChangesAsync();
        return (connection.Id, approvalId, connection.Version, active, candidate);
    }

    private static ApprovalDecided CredentialDecision(Guid approvalId, Guid connectionId, long version) => new(
        Guid.NewGuid(), approvalId, "merchant", MerchantId, "approved", CheckerId, "checked",
        "psp-credential-version", connectionId.ToString("D"), $"v{version}", "corr-approval", Now);

    private AdminPaymentsApprovalExecutor CredentialExecutor(MerchantRuntimeDbContext db, bool probeSucceeds) => new(
        db, new FixedClock(), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        CredentialVault(db), new OkAdapterFactory(probeSucceeds), NoOpSecurityTelemetry.Instance,
        new PaymentAuthorizationSqlLockManager(db));

    private LocalEnvelopeVaultStore CredentialVault(MerchantRuntimeDbContext db) => new(
        db, new FixedClock(), _keyring, new NoopAuditWriter());

    private readonly VaultKeyring _keyring = new(VaultOptions.LegacyKeyId,
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { [VaultOptions.LegacyKeyId] = RandomNumberGenerator.GetBytes(32) });

    private async Task<(Guid RulesetId, Guid ApprovalId, long Version)> SeedPendingRulesetAsync()
    {
        var connection = Connection.Create(
            MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "approval-secret", Now);
        var ruleset = RoutingRuleset.Create(MerchantId, "Approval", [new RoutingRuleSpec(
            1, PaymentMethods.Card, null, null, null, connection.Id, null, false)], Now);
        var approvalId = Guid.NewGuid();
        ruleset.RequestActivation(approvalId, Now);

        await using var db = NewContext();
        db.Merchants.Add(Merchant.CreateWithId(
            MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [PaymentMethods.Card], "{}", Now));
        db.PspConnections.Add(connection);
        db.RoutingRulesets.Add(ruleset);
        await db.SaveChangesAsync();
        return (ruleset.Id, approvalId, ruleset.Version);
    }

    private static ApprovalDecided Decision(Guid eventId, Guid approvalId, Guid rulesetId, long version) => new(
        eventId, approvalId, "merchant", MerchantId, "approved", CheckerId, "checked",
        "routing-ruleset", rulesetId.ToString("D"), $"v{version}", "corr-approval", Now);

    private static AdminPaymentsApprovalExecutor Executor(
        MerchantRuntimeDbContext db, ISecurityTelemetry telemetry) => new(
        db, new FixedClock(), new MerchantRuntimeUnitOfWork(db, telemetry), null!, null!, telemetry,
        new PaymentAuthorizationSqlLockManager(db));

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        FakeActorContext.For(MerchantId), FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class CaptureTelemetry : ISecurityTelemetry
    {
        public List<DenialEvent> Events { get; } = [];
        public void Emit(DenialEvent evt) => Events.Add(evt);
    }

    private sealed class NoopAuditWriter : IVaultRevealAuditWriter
    {
        public Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OkAdapterFactory(bool probeSucceeds) : IPspAdapterFactory
    {
        private readonly OkAdapter _adapter = new(probeSucceeds);
        public IPspAdapter For(Code requested) => _adapter;
    }

    private sealed class OkAdapter(bool probeSucceeds) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string> { PaymentMethods.Card };

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken ct) =>
            probeSucceeds
                ? Task.FromResult(new PspProbeResult("authenticated", "ok"))
                : throw new InvalidOperationException("probe failed");

        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, PspEnvironment environment, CancellationToken ct) =>
            throw new NotSupportedException();
        public bool VerifyWebhook(string rawPayload, string signature, string secret) => throw new NotSupportedException();
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, PspEnvironment environment, CancellationToken ct) =>
            throw new NotSupportedException();
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }

    public void Dispose() => _connection.Dispose();
}
