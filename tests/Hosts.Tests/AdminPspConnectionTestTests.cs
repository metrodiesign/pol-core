using BuildingBlocks.Application;
using Merchants.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;

namespace Hosts.Tests;

public sealed class AdminPspConnectionTestTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("a1000000-0000-4000-8000-000000000001");
    private static readonly Guid ActorId = Guid.Parse("a2000000-0000-4000-8000-000000000001");
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public AdminPspConnectionTestTests()
    {
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
    }

    [Fact]
    public async Task Missing_vault_secret_is_recorded_as_failed_probe_instead_of_escaping_as_500()
    {
        var connection = Connection.Create(
            MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "missing-secret", Now);
        await using var db = NewContext();
        db.Merchants.Add(Merchant.CreateWithId(
            MerchantId, "vcommerce", "Merchant", null, "TH", "THB", ["card"], "{}", Now));
        db.PspConnections.Add(connection);
        await db.SaveChangesAsync();

        var adapters = new UnusedAdapterFactory();
        var store = new AdminPaymentsControlStore(
            db,
            new FixedClock(),
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            new MissingVault(),
            new UnusedEnvelopeFactory(),
            adapters);

        var failure = await Assert.ThrowsAsync<PspConnectionTestFailedException>(() => store.TestConnectionAsync(
            new TestPspConnectionIntent(
                connection.Id, MerchantId, connection.Version, "probe-missing-secret",
                new AdminPaymentsAccess(ActorId, true, new HashSet<Guid>())),
            default));

        Assert.Equal("failed", failure.Connection.Health);
        Assert.Equal("probe_failed", failure.Connection.LastTestResult);
        Assert.Equal(Now, failure.Connection.LastTestedAt);
        Assert.False(adapters.Adapter.ProbeWasCalled);

        var operation = await db.AdminOperationRecords.SingleAsync();
        Assert.Equal(502, operation.HttpStatus);
    }

    [Fact]
    public async Task Replacing_routing_rules_replaces_children_and_advances_version()
    {
        var connection = Connection.Create(
            MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "routing-secret", Now);
        await using (var seed = NewContext())
        {
            seed.Merchants.Add(Merchant.CreateWithId(
                MerchantId, "vcommerce", "Merchant", null, "TH", "THB", ["card"], "{}", Now));
            seed.PspConnections.Add(connection);
            await seed.SaveChangesAsync();
        }

        var access = new AdminPaymentsAccess(ActorId, true, new HashSet<Guid>());
        RoutingRulesetView created;
        await using (var createDb = NewContext())
        {
            var store = Store(createDb);
            created = await store.CreateRulesetAsync(new CreateRoutingRulesetIntent(
                MerchantId, "Draft", [Rule(connection.Id, 1)], access), default);
        }

        await using var replaceDb = NewContext();
        var replaced = await Store(replaceDb).ReplaceRulesetAsync(new ReplaceRoutingRulesetIntent(
            created.RulesetId, MerchantId, "Updated", [Rule(connection.Id, 10)], created.Version, access), default);

        Assert.Equal(2, replaced.Version);
        Assert.Equal("Updated", replaced.Name);
        Assert.Equal(10, Assert.Single(replaced.Rules).Priority);
    }

    private static RoutingRuleInput Rule(Guid connectionId, int priority) =>
        new(priority, "card", null, 1m, 9999m, connectionId, null, false);

    private static AdminPaymentsControlStore Store(MerchantRuntimeDbContext db) => new(
        db,
        new FixedClock(),
        new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        new MissingVault(),
        new UnusedEnvelopeFactory(),
        new UnusedAdapterFactory());

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        new Actor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    public void Dispose() => _connection.Dispose();

    private sealed class Actor : IActorContext
    {
        public Guid MerchantId => AdminPspConnectionTestTests.MerchantId;
        public Guid? UserId => ActorId;
        public bool HasActor => true;
    }

    private sealed class AllowAllWrites : IWriteAuthorizer
    {
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => Now;
    }

    private sealed class MissingVault : IVaultSecretStore
    {
        public Task StoreAsync(Guid merchantId, string name, string secret, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task InsertAsync(Guid merchantId, string name, string secret, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<string> RevealAsync(Guid merchantId, string name, CancellationToken ct) =>
            throw new KeyNotFoundException("Secret is absent.");
        public Task<string?> MaskedAsync(Guid merchantId, string name, CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<bool> ExistsAsync(Guid merchantId, string name, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private sealed class UnusedEnvelopeFactory : IPspSecretEnvelopeFactory
    {
        public PspSecretEnvelopeResult Build(PspSecretInput input) => throw new NotSupportedException();
    }

    private sealed class UnusedAdapterFactory : IPspAdapterFactory
    {
        public ProjectionOnlyAdapter Adapter { get; } = new();

        public IPspAdapter For(Code psp) => Adapter;
    }

    internal sealed class ProjectionOnlyAdapter : IPspAdapter
    {
        public bool ProbeWasCalled { get; private set; }
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string> { PaymentMethods.Card };

        public Task<PspProbeResult> TestConnectionAsync(string secret, CancellationToken ct)
        {
            ProbeWasCalled = true;
            throw new InvalidOperationException("Adapter must not run without a vault secret.");
        }

        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, CancellationToken ct) =>
            throw new NotSupportedException();
        public bool VerifyWebhook(string rawPayload, string signature, string secret) =>
            throw new NotSupportedException();
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, CancellationToken ct) =>
            throw new NotSupportedException();
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }
}
