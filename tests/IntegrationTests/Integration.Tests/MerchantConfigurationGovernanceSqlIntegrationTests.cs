using BuildingBlocks.Application;
using Contracts;
using Governance.Application;
using Governance.Domain;
using Microsoft.EntityFrameworkCore;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Governance;

namespace Integration.Tests;

[Trait("Category", "Integration")]
[Trait("Capability", "MerchantConfiguration")]
[Trait("Requirement", "REQ-5.4")]
[Trait("Requirement", "REQ-5.5")]
public sealed class MerchantConfigurationGovernanceSqlIntegrationTests
{
    private const string Database = "PolMerchantConfigTask3CiTest";

    [Fact]
    public async Task Sql_server_allows_one_checker_and_rejects_the_concurrent_loser()
    {
        await ProvisionDatabaseAsync();
        try
        {
            await using var first = NewContext();
            var approvalId = Guid.NewGuid();
            var makerId = Guid.NewGuid();
            var request = new ApprovalRequested(
                Guid.NewGuid(), approvalId, "merchant", Guid.NewGuid(),
                "psp.environment.change", "settings.manage", makerId,
                "merchant-environment", Guid.NewGuid().ToString("D"), "v1", "task3-sql", Clock.UtcNow);
            var firstStore = NewStore(first);
            await firstStore.ReceiveAsync(request, default);

            await using var second = NewContext();
            var secondStore = NewStore(second);
            var checkerA = Guid.NewGuid();
            var checkerB = Guid.NewGuid();
            var results = await Task.WhenAll(
                DecideAsync(firstStore, approvalId, checkerA, "decision-a"),
                DecideAsync(secondStore, approvalId, checkerB, "decision-b"));

            Assert.Equal(1, results.Count(x => x is not null));
            var failure = results.Single(x => x is null);
            Assert.Null(failure);

            await using var verify = NewContext();
            var approval = await verify.ApprovalRequests.SingleAsync(x => x.Id == approvalId);
            Assert.Equal(ApprovalStatus.Approved, approval.Status);
            Assert.True(approval.CheckerId == checkerA || approval.CheckerId == checkerB);
            Assert.Equal(2, await verify.ApprovalEvents.CountAsync(x => x.ApprovalId == approvalId));
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(Database);
        }
    }

    // The named catalog is created, fully migrated, and dropped by this test alone; a pre-create drop
    // clears a leftover from an aborted run. The sibling scratch helpers own the create/migrate/drop
    // SQL so no new provisioning abstraction is added here.
    private static async Task ProvisionDatabaseAsync()
    {
        try
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(Database);
        }
        catch
        {
            // First run has no database; a later run cleans a leftover here.
        }

        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(Database);
        await using var migration = PaymentCapabilitySchemaIntegrationTests.CreateContext(Database);
        await migration.Database.MigrateAsync();
    }

    private static async Task<DecisionResult?> DecideAsync(
        GovernanceStore store, Guid approvalId, Guid checkerId, string idempotencyKey)
    {
        try
        {
            return await store.DecideAsync(new DecisionIntent(
                approvalId,
                ApprovalDecision.Approve,
                "checked",
                1,
                "v1",
                idempotencyKey,
                "task3-sql-decision",
                new GovernanceAccess(
                    checkerId,
                    IsUnrestricted: true,
                    Merchants: new HashSet<Guid>(),
                    Permissions: new HashSet<string>(["settings.manage"], StringComparer.Ordinal))), default);
        }
        catch (ConflictException)
        {
            return null;
        }
        catch (ConcurrencyConflictException)
        {
            return null;
        }
    }

    private static ControlPlaneDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(Database), sql => sql.UseCompatibilityLevel(170))
            .Options,
        new AllowAllWrites(),
        NoopTelemetry.Instance);

    private static GovernanceStore NewStore(ControlPlaneDbContext db) => new(
        db,
        new ControlPlaneUnitOfWork(db, NoopTelemetry.Instance),
        Clock,
        new DisabledAuditAnchorStore(),
        new GovernanceSqlLockManager(db),
        new GovernanceAuditAppender(db, new GovernanceSqlLockManager(db)));

    private static readonly FixedClock Clock = new();

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 9, 10, 5, 0, 0, DateTimeKind.Utc);
    }

    private sealed class AllowAllWrites : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class NoopTelemetry : ISecurityTelemetry
    {
        public static readonly NoopTelemetry Instance = new();
        public void Emit(DenialEvent denialEvent) { }
    }

    private sealed class DisabledAuditAnchorStore : IAuditAnchorStore
    {
        public bool IsEnabled => false;
        public Task<IReadOnlyDictionary<string, AuditAnchorCheckpoint>> ReadAllLatestAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, AuditAnchorCheckpoint>>(
                new Dictionary<string, AuditAnchorCheckpoint>());
        public Task AppendAsync(AuditAnchorCheckpoint checkpoint, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
