using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using Merchants.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;

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

    public void Dispose() => _connection.Dispose();
}
