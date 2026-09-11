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
using Persistence.ControlPlane.Payments;
using Persistence.ControlPlane.Vault;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using SharedKernel;

namespace Architecture.Tests;

[Trait("Capability", "MerchantConfiguration")]
[Trait("Requirement", "REQ-5.3")]
[Trait("Requirement", "REQ-5.5")]
[Trait("Requirement", "REQ-5.6")]
public sealed class AdminPaymentsApprovalExecutorTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("e1000000-0000-4000-8000-000000000001");
    private static readonly Guid CheckerId = Guid.Parse("e2000000-0000-4000-8000-000000000001");
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public AdminPaymentsApprovalExecutorTests()
    {
        _connection.Open();
        RuntimeSchema.EnsureCreated(_connection);
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

        var outbox = Assert.Single(await db.GovernanceOutboxMessages
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
        Assert.Empty(await verify.GovernanceOutboxMessages
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

    [Fact]
    public async Task Approving_an_environment_change_activates_every_connection_and_flips_the_merchant()
    {
        var seed = await SeedPendingEnvironmentAsync(candidateExpiresAt: Now.AddHours(24));
        await using var db = NewContext();
        var executor = EnvironmentExecutor(db, CredentialVault(db));

        await executor.ExecuteAsync(EnvironmentDecision(seed.ApprovalId, seed.Version), default);

        await using var verify = NewContext();
        var merchant = await verify.Merchants.SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Live, merchant.PaymentEnvironment);                 // AC-7.2 environment flipped
        Assert.Null(merchant.PendingPaymentEnvironment);
        Assert.Null(merchant.PendingPaymentEnvironmentApprovalId);
        for (var i = 0; i < seed.ConnectionIds.Length; i++)
        {
            var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionIds[i]);
            Assert.Equal(seed.CandidateVersionIds[i], connection.ActiveSecretVersionId); // candidate activated
            Assert.Equal(PspEnvironment.Live, connection.ActiveSecretEnvironment);
            Assert.Null(connection.PendingApprovalId);
            var previous = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.ActiveVersionIds[i]);
            Assert.Equal(VaultSecretVersionState.Retired, previous.State);               // old retired, not deleted
            Assert.Null(previous.ExpiresAt);
        }
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("environment_activated", execution.Outcome);
    }

    [Fact]
    public async Task A_failure_midway_through_activation_rolls_the_whole_switch_back()
    {
        var seed = await SeedPendingEnvironmentAsync(candidateExpiresAt: Now.AddHours(24));
        await using var db = NewContext();
        // Throw on the SECOND activation: the first connection is already retired+activated in-transaction when
        // the failure lands, so this proves the whole switch — both connections AND the merchant — rolls back
        // (REQ-2.14/2.15, critical #2), not just the connection that failed.
        var vault = new FailOnNthActivateVault(CredentialVault(db), failOn: 2);
        var executor = EnvironmentExecutor(db, vault);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            executor.ExecuteAsync(EnvironmentDecision(seed.ApprovalId, seed.Version), default));

        await using var verify = NewContext();
        var merchant = await verify.Merchants.SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Sandbox, merchant.PaymentEnvironment);              // unchanged
        Assert.Equal(PspEnvironment.Live, merchant.PendingPaymentEnvironment);          // still pending
        Assert.Equal(seed.ApprovalId, merchant.PendingPaymentEnvironmentApprovalId);
        for (var i = 0; i < seed.ConnectionIds.Length; i++)
        {
            var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionIds[i]);
            Assert.Equal(seed.ActiveVersionIds[i], connection.ActiveSecretVersionId);   // active kept
            Assert.Equal(PspEnvironment.Sandbox, connection.ActiveSecretEnvironment);
            Assert.Equal(seed.ApprovalId, connection.PendingApprovalId);                // still staged
            var previous = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.ActiveVersionIds[i]);
            var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.CandidateVersionIds[i]);
            Assert.Equal(VaultSecretVersionState.Active, previous.State);
            Assert.Equal(VaultSecretVersionState.Staged, candidate.State);
        }
        Assert.Empty(await verify.ApprovalExecutionRecords.ToListAsync());              // claim rolled back too
    }

    [Fact]
    public async Task Rejecting_an_environment_change_discards_every_candidate_and_keeps_the_environment()
    {
        var seed = await SeedPendingEnvironmentAsync(candidateExpiresAt: Now.AddHours(24));
        await using var db = NewContext();
        var executor = EnvironmentExecutor(db, CredentialVault(db));
        var rejected = EnvironmentDecision(seed.ApprovalId, seed.Version) with { Decision = "rejected" };

        await executor.ExecuteAsync(rejected, default);

        await using var verify = NewContext();
        var merchant = await verify.Merchants.SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Sandbox, merchant.PaymentEnvironment);              // AC-7.3 environment kept
        Assert.Null(merchant.PendingPaymentEnvironment);
        for (var i = 0; i < seed.ConnectionIds.Length; i++)
        {
            var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionIds[i]);
            Assert.Equal(seed.ActiveVersionIds[i], connection.ActiveSecretVersionId);
            Assert.Null(connection.PendingApprovalId);
            var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.CandidateVersionIds[i]);
            Assert.Equal(VaultSecretVersionState.Discarded, candidate.State);           // every candidate discarded
        }
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("environment_rejected", execution.Outcome);
    }

    [Fact]
    public async Task Approving_a_stale_environment_change_discards_every_candidate_and_reports_stale()
    {
        var seed = await SeedPendingEnvironmentAsync(candidateExpiresAt: Now.AddHours(24));
        await using var db = NewContext();
        var executor = EnvironmentExecutor(db, CredentialVault(db));

        // The merchant version moved since the request (e.g. an unrelated settings edit): the approval is stale
        // and must be cleaned up like a reject (AC-7.3 "reject OR stale"), not left pending on a rollback.
        await executor.ExecuteAsync(EnvironmentDecision(seed.ApprovalId, seed.Version + 1), default);

        await using var verify = NewContext();
        var merchant = await verify.Merchants.SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Sandbox, merchant.PaymentEnvironment);
        Assert.Null(merchant.PendingPaymentEnvironment);
        for (var i = 0; i < seed.ConnectionIds.Length; i++)
        {
            var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionIds[i]);
            Assert.Equal(seed.ActiveVersionIds[i], connection.ActiveSecretVersionId);
            Assert.Null(connection.PendingApprovalId);
            var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.CandidateVersionIds[i]);
            Assert.Equal(VaultSecretVersionState.Discarded, candidate.State);           // AC-7.3 candidates discarded
        }
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("environment_stale", execution.Outcome);
    }

    [Fact]
    public async Task Approving_an_environment_change_with_an_expired_candidate_reports_credential_candidate_expired()
    {
        var seed = await SeedPendingEnvironmentAsync(candidateExpiresAt: Now.AddHours(-1));  // AC-7.4 past 24h
        await using var db = NewContext();
        var executor = EnvironmentExecutor(db, CredentialVault(db));

        await executor.ExecuteAsync(EnvironmentDecision(seed.ApprovalId, seed.Version), default);

        await using var verify = NewContext();
        var merchant = await verify.Merchants.SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Sandbox, merchant.PaymentEnvironment);              // whole request fails safe
        Assert.Null(merchant.PendingPaymentEnvironment);
        for (var i = 0; i < seed.ConnectionIds.Length; i++)
        {
            var connection = await verify.PspConnections.SingleAsync(x => x.Id == seed.ConnectionIds[i]);
            Assert.Equal(seed.ActiveVersionIds[i], connection.ActiveSecretVersionId);   // active untouched
            Assert.Null(connection.PendingApprovalId);
            var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == seed.CandidateVersionIds[i]);
            Assert.Equal(VaultSecretVersionState.Discarded, candidate.State);
        }
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("credential_candidate_expired", execution.Outcome);
    }

    [Fact]
    public async Task Approving_an_environment_change_with_a_connection_staged_by_no_one_is_incomplete()
    {
        // The request staged the ONE connection the merchant had; a second connection (a different PSP) was
        // then created before approval and holds no candidate for this approval. Activating the staged one and
        // flipping the merchant would leave the new connection mixed, so the executor fails the whole request
        // (design 250-263, critical #2) rather than partially switch.
        Guid stagedConnection;
        Guid approvalId;
        long version;
        Guid candidateVersion;
        await using (var seedDb = NewContext())
        {
            var vault = CredentialVault(seedDb);
            seedDb.Merchants.Add(Merchant.CreateWithId(
                MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [], "{}", Now));
            var conn = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp-only", Now);
            seedDb.PspConnections.Add(conn);
            await seedDb.SaveChangesAsync();
            var active = await vault.StageVersionAsync(MerchantId, "psp-only", "{\"secretKey\":\"active\"}", "{}", Now.AddHours(24), default);
            await seedDb.SaveChangesAsync();
            await vault.ActivateVersionAsync(MerchantId, active, default);
            conn.SetInitialSecretVersion(active, PspEnvironment.Sandbox);
            await seedDb.SaveChangesAsync();
            candidateVersion = await vault.StageVersionAsync(MerchantId, "psp-only", "{\"secretKey\":\"candidate\"}", "{}", Now.AddHours(24), default);
            approvalId = Guid.NewGuid();
            conn.StageSecretVersion(candidateVersion, approvalId, PspEnvironment.Live);
            var seedMerchant = await seedDb.Merchants.SingleAsync(x => x.Id == MerchantId);
            seedMerchant.StagePaymentEnvironment(PspEnvironment.Live, approvalId);
            await seedDb.SaveChangesAsync();
            stagedConnection = conn.Id;
            version = seedMerchant.Version;
            // A connection of a different PSP appears AFTER the request — never staged for this approval.
            seedDb.PspConnections.Add(Connection.Create(MerchantId, Code.Omise, string.Empty, "psp-late", Now));
            await seedDb.SaveChangesAsync();
        }
        await using var db = NewContext();
        var executor = EnvironmentExecutor(db, CredentialVault(db));

        await executor.ExecuteAsync(EnvironmentDecision(approvalId, version), default);

        await using var verify = NewContext();
        var merchant = await verify.Merchants.SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Sandbox, merchant.PaymentEnvironment);
        Assert.Null(merchant.PendingPaymentEnvironment);
        var staged = await verify.PspConnections.SingleAsync(x => x.Id == stagedConnection);
        Assert.Null(staged.PendingApprovalId);                                          // its candidate discarded
        var candidate = await verify.Set<VaultSecretVersion>().SingleAsync(x => x.Id == candidateVersion);
        Assert.Equal(VaultSecretVersionState.Discarded, candidate.State);
        var execution = Assert.Single(await verify.ApprovalExecutionRecords.ToListAsync());
        Assert.Equal("environment_credentials_incomplete", execution.Outcome);
    }

    private async Task<(Guid[] ConnectionIds, Guid ApprovalId, long Version, Guid[] ActiveVersionIds, Guid[] CandidateVersionIds)>
        SeedPendingEnvironmentAsync(DateTime candidateExpiresAt)
    {
        await using var db = NewContext();
        var vault = CredentialVault(db);
        db.Merchants.Add(Merchant.CreateWithId(
            MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [], "{}", Now));
        var connA = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "psp-a", Now);
        var connB = Connection.Create(MerchantId, Code.Omise, string.Empty, "psp-b", Now);
        db.PspConnections.AddRange(connA, connB);
        await db.SaveChangesAsync();

        var connections = new[] { (Conn: connA, Name: "psp-a"), (Conn: connB, Name: "psp-b") };
        var actives = new Guid[connections.Length];
        var candidates = new Guid[connections.Length];
        var approvalId = Guid.NewGuid();
        for (var i = 0; i < connections.Length; i++)
        {
            actives[i] = await vault.StageVersionAsync(MerchantId, connections[i].Name,
                $"{{\"secretKey\":\"active-{connections[i].Name}\"}}", "{}", Now.AddHours(24), default);
            await db.SaveChangesAsync();
            await vault.ActivateVersionAsync(MerchantId, actives[i], default);
            connections[i].Conn.SetInitialSecretVersion(actives[i], PspEnvironment.Sandbox);
        }
        await db.SaveChangesAsync();
        for (var i = 0; i < connections.Length; i++)
        {
            candidates[i] = await vault.StageVersionAsync(MerchantId, connections[i].Name,
                $"{{\"secretKey\":\"candidate-{connections[i].Name}\"}}", "{}", candidateExpiresAt, default);
            connections[i].Conn.StageSecretVersion(candidates[i], approvalId, PspEnvironment.Live);
        }
        var merchant = await db.Merchants.SingleAsync(x => x.Id == MerchantId);
        merchant.StagePaymentEnvironment(PspEnvironment.Live, approvalId);
        await db.SaveChangesAsync();
        return ([connA.Id, connB.Id], approvalId, merchant.Version, actives, candidates);
    }

    private static ApprovalDecided EnvironmentDecision(Guid approvalId, long version) => new(
        Guid.NewGuid(), approvalId, "merchant", MerchantId, "approved", CheckerId, "checked",
        "merchant-environment", MerchantId.ToString("D"), $"v{version}", "corr-approval", Now);

    private AdminPaymentsApprovalExecutor EnvironmentExecutor(ControlPlaneDbContext db, IVaultSecretStore vault) => new(
        db, new FixedClock(), new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        vault, new OkAdapterFactory(true), NoOpSecurityTelemetry.Instance,
        new Persistence.ControlPlane.Payments.PaymentAuthorizationSqlLockManager(db));

    private sealed class FailOnNthActivateVault(IVaultSecretStore inner, int failOn) : IVaultSecretStore
    {
        private int _activations;

        public Task ActivateVersionAsync(Guid merchantId, Guid versionId, CancellationToken ct)
        {
            if (++_activations == failOn)
                throw new InvalidOperationException("Injected vault activation failure.");
            return inner.ActivateVersionAsync(merchantId, versionId, ct);
        }

        public Task StoreAsync(Guid m, string n, string s, CancellationToken ct) => inner.StoreAsync(m, n, s, ct);
        public Task InsertAsync(Guid m, string n, string s, CancellationToken ct) => inner.InsertAsync(m, n, s, ct);
        public Task<string> RevealAsync(Guid m, string n, CancellationToken ct) => inner.RevealAsync(m, n, ct);
        public Task<string?> MaskedAsync(Guid m, string n, CancellationToken ct) => inner.MaskedAsync(m, n, ct);
        public Task<bool> ExistsAsync(Guid m, string n, CancellationToken ct) => inner.ExistsAsync(m, n, ct);
        public Task<Guid> StageVersionAsync(Guid m, string n, string s, string h, DateTime? e, CancellationToken ct) =>
            inner.StageVersionAsync(m, n, s, h, e, ct);
        public Task<string> ReadVersionForServerAsync(Guid m, Guid v, CancellationToken ct) =>
            inner.ReadVersionForServerAsync(m, v, ct);
        public Task RetireVersionAsync(Guid m, Guid v, CancellationToken ct) => inner.RetireVersionAsync(m, v, ct);
        public Task DiscardVersionAsync(Guid m, Guid v, CancellationToken ct) => inner.DiscardVersionAsync(m, v, ct);
        public Task<string?> MaskedVersionAsync(Guid m, Guid v, CancellationToken ct) => inner.MaskedVersionAsync(m, v, ct);
        public Task<DateTime?> StagedVersionExpiresAtAsync(Guid m, Guid v, CancellationToken ct) =>
            inner.StagedVersionExpiresAtAsync(m, v, ct);
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

    private AdminPaymentsApprovalExecutor CredentialExecutor(ControlPlaneDbContext db, bool probeSucceeds) => new(
        db, new FixedClock(), new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        CredentialVault(db), new OkAdapterFactory(probeSucceeds), NoOpSecurityTelemetry.Instance,
        new Persistence.ControlPlane.Payments.PaymentAuthorizationSqlLockManager(db));

    private LocalEnvelopeVaultStore CredentialVault(ControlPlaneDbContext db) => new(
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

    private AdminPaymentsApprovalExecutor Executor(
        ControlPlaneDbContext db, ISecurityTelemetry telemetry) => new(
        db, new FixedClock(),
        new ControlPlaneUnitOfWork(db, telemetry), null!, null!, telemetry,
        new Persistence.ControlPlane.Payments.PaymentAuthorizationSqlLockManager(db));

    private ControlPlaneDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options,
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

    public void Dispose()
    {
        _connection.Dispose();
    }
}
