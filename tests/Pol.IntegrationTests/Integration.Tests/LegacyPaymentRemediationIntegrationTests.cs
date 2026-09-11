using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using Persistence.ControlPlane.Payments;
using Persistence.MerchantRuntime;
using Persistence.ControlPlane.Vault;
using SharedKernel;

namespace Integration.Tests;

/// <summary>
/// merchant-psp-settings task 9 (AC-9.1 backfill, AC-9.2/9.3 session remediation, AC-9.6/9.7 migration
/// forward + rollback). Proves the offline remediation seam against a real SQL Server: the environment
/// backfill only touches merchants that never switched, an uncharged legacy Session upgrades to the current
/// route, a charged one upgrades only when exactly one historical secret proves the charge read-only, and the
/// vault-expiry migration clears the lapsed expiry off active/retired versions with a no-op Down that a
/// rollback build reads through.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LegacyPaymentRemediationIntegrationTests
{
    private const string PreviousMigration = "20260907023022_InboundWebhookPendingMatch";
    private static readonly Guid ActorId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly byte[] MasterKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    // --- AC-9.1: bootstrap Merchant.PaymentEnvironment from the retired global default ---

    [Fact]
    public async Task Backfill_sets_the_global_environment_only_for_merchants_that_never_switched()
    {
        var database = $"pol_legacy_backfill_{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using var migrate = PaymentCapabilitySchemaIntegrationTests.CreateContext(database);
            await migrate.GetService<IMigrator>().MigrateAsync();

            var neverSwitched = Guid.NewGuid();
            var alreadyLive = Guid.NewGuid();
            var alreadySwitchedSandbox = Guid.NewGuid();
            await using (var sql = await OpenAsync(database))
            {
                // MigrateAsync() also runs SeedData, which inserts a merchant that the expand migration marks
                // never-switched (PaymentEnvironmentUpdatedAt = CreatedAt) at the sandbox default — it would
                // otherwise qualify for the global backfill too. Mark every pre-existing row as already-switched
                // so this test asserts on exactly the three merchants it seeds below.
                await IntegrationDb.ExecAsync(sql,
                    "UPDATE merch.Merchants SET PaymentEnvironmentUpdatedAt = DATEADD(hour, 1, CreatedAt);");
                // never switched: PaymentEnvironmentUpdatedAt == CreatedAt, still sandbox.
                await SeedMerchantAsync(sql, neverSwitched, environment: 1, switched: false);
                // already switched to live: must not be re-touched (it is already the target anyway).
                await SeedMerchantAsync(sql, alreadyLive, environment: 2, switched: true);
                // already switched but chose sandbox: must NOT be flipped to live by the backfill.
                await SeedMerchantAsync(sql, alreadySwitchedSandbox, environment: 1, switched: true);
            }

            await using var db = ControlPlaneContext(database, neverSwitched);
            await using var commerce = CommerceContext(database, neverSwitched);
            var service = Service(db, commerce, out _, out _);
            var first = await service.BackfillMerchantEnvironmentsAsync(ActorId, PspEnvironment.Live, default);
            var second = await service.BackfillMerchantEnvironmentsAsync(ActorId, PspEnvironment.Live, default);

            Assert.Equal(1, first);   // only the never-switched merchant
            Assert.Equal(0, second);  // idempotent re-run

            await using var verify = await OpenAsync(database);
            Assert.Equal(2, await EnvironmentOfAsync(verify, neverSwitched));         // -> live
            Assert.Equal(2, await EnvironmentOfAsync(verify, alreadyLive));           // unchanged
            Assert.Equal(1, await EnvironmentOfAsync(verify, alreadySwitchedSandbox)); // left as its choice
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    // --- AC-9.2: an uncharged legacy Session is re-routed and upgraded to version 1 ---

    [Fact]
    public async Task An_uncharged_legacy_session_is_upgraded_with_the_current_route()
    {
        var database = $"pol_legacy_nocharge_{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using var migrate = PaymentCapabilitySchemaIntegrationTests.CreateContext(database);
            await migrate.GetService<IMigrator>().MigrateAsync();

            var merchantId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            var secretVersionId = Guid.NewGuid();
            await using (var sql = await OpenAsync(database))
            {
                await SeedMerchantAsync(sql, merchantId, environment: 1, switched: false);
                var sessionId = Guid.NewGuid();
                await SeedLegacyV0SessionAsync(sql, merchantId, sessionId, chargeId: null);
            }

            await using var db = ControlPlaneContext(database, merchantId);
            await using var commerce = CommerceContext(database, merchantId);
            var route = new FakeRouteSelector(new PspRouteSelection(
                connectionId, Code.TwoCTwoP, secretVersionId, PspEnvironment.Sandbox));
            var service = ServiceWith(db, commerce, route, new FakeAdapterFactory());

            var report = await service.RemediateSessionsAsync(ActorId, PspEnvironment.Sandbox, default);

            Assert.Equal(1, report.Upgraded);
            Assert.Equal(0, report.Blocked);
            await using var verify = await OpenAsync(database);
            Assert.Equal(0, await LegacyCountAsync(verify, merchantId));
            Assert.Equal(connectionId, await PinnedConnectionAsync(verify, merchantId));
            Assert.Equal(secretVersionId, await PinnedSecretVersionAsync(verify, merchantId));
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    // --- AC-9.3: a charged legacy Session is proven read-only, one historical secret at a time ---

    [Fact]
    public async Task A_charged_legacy_session_is_upgraded_when_exactly_one_secret_confirms()
    {
        var result = await RemediateChargedAsync(
            confirms: secret => secret == "SECRET-ACTIVE",
            confirmedAmount: Money.Of(100, "THB"));

        Assert.Equal(1, result.Report.Upgraded);
        Assert.Equal(0, result.Report.Blocked);
        Assert.Equal(0, result.RemainingLegacy);
        Assert.Equal(result.ActiveVersionId, result.PinnedSecretVersionId);
    }

    [Fact]
    public async Task A_charged_legacy_session_stays_v0_when_two_secrets_confirm()
    {
        // Two historical secrets both read the charge — ambiguous, so no single version can be pinned
        // (design step 9). The row stays version 0 and keeps blocking activation.
        var result = await RemediateChargedAsync(
            confirms: _ => true,
            confirmedAmount: Money.Of(100, "THB"));

        Assert.Equal(0, result.Report.Upgraded);
        Assert.Equal(1, result.Report.Blocked);
        Assert.Equal(1, result.RemainingLegacy);
    }

    [Fact]
    public async Task A_charged_legacy_session_stays_v0_when_no_secret_confirms_the_amount()
    {
        // The active secret reads the charge but the PSP reports a different amount, and the retired secret
        // cannot read it at all — nothing proves the historical snapshot, so the row stays version 0.
        var result = await RemediateChargedAsync(
            confirms: secret => secret == "SECRET-ACTIVE",
            confirmedAmount: Money.Of(999, "THB"));

        Assert.Equal(0, result.Report.Upgraded);
        Assert.Equal(1, result.Report.Blocked);
        Assert.Equal(1, result.RemainingLegacy);
    }

    private sealed record ChargedResult(
        LegacySessionRemediationReport Report, int RemainingLegacy, Guid ActiveVersionId, Guid? PinnedSecretVersionId);

    private async Task<ChargedResult> RemediateChargedAsync(Func<string, bool> confirms, Money confirmedAmount)
    {
        var database = $"pol_legacy_charged_{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using var migrate = PaymentCapabilitySchemaIntegrationTests.CreateContext(database);
            await migrate.GetService<IMigrator>().MigrateAsync();

            var merchantId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            await using (var sql = await OpenAsync(database))
            {
                await SeedMerchantAsync(sql, merchantId, environment: 2, switched: true);
                await SeedConnectionAsync(sql, merchantId, connectionId, Code.TwoCTwoP);
                await SeedLegacyV0SessionAsync(sql, merchantId, Guid.NewGuid(), chargeId: "chrg_legacy_1");
            }

            // Two readable historical secrets on the connection: an active version and a retired one.
            var secretName = $"psp-connection-{connectionId:N}";
            Guid activeVersionId;
            await using (var seed = ControlPlaneContext(database, merchantId))
            {
                var keyring = new VaultKeyring("k1", new Dictionary<string, byte[]> { ["k1"] = MasterKey });
                var vault = new LocalEnvelopeVaultStore(seed, new FixedClock(), keyring, new NoopAuditWriter());
                var retiredId = await vault.StageVersionAsync(merchantId, secretName, "SECRET-RETIRED", "hint", null, default);
                await vault.ActivateVersionAsync(merchantId, retiredId, default);
                // Persist v1 before staging the next version: StageVersionAsync numbers off the MAX(Version) in
                // the DB (not the local tracker), exactly as production does where each rotation is its own saved
                // unit of work. Without this commit both stages would read MAX = 0 and collide on version 1.
                await seed.SaveChangesAsync();
                activeVersionId = await vault.StageVersionAsync(merchantId, secretName, "SECRET-ACTIVE", "hint", null, default);
                await vault.RetireVersionAsync(merchantId, retiredId, default);
                await vault.ActivateVersionAsync(merchantId, activeVersionId, default);
                await seed.SaveChangesAsync();
            }

            await using var db = ControlPlaneContext(database, merchantId);
            await using var commerce = CommerceContext(database, merchantId);
            var adapters = new FakeAdapterFactory(new FakeChargeAdapter(confirms, confirmedAmount));
            var service = ServiceWith(db, commerce, new FakeRouteSelector(null), adapters);

            var report = await service.RemediateSessionsAsync(ActorId, PspEnvironment.Live, default);

            await using var verify = await OpenAsync(database);
            return new ChargedResult(
                report,
                await LegacyCountAsync(verify, merchantId),
                activeVersionId,
                await PinnedSecretVersionAsync(verify, merchantId));
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    // --- AC-9.1 vault fix + AC-9.6 rollback: the expiry migration clears active/retired, Down is a no-op ---

    [Fact]
    public async Task The_vault_expiry_migration_clears_active_and_retired_expiry_but_not_staged()
    {
        var database = $"pol_legacy_vault_{Guid.NewGuid():N}";
        await PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        try
        {
            await using var context = PaymentCapabilitySchemaIntegrationTests.CreateContext(database);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            var merchantId = Guid.NewGuid();
            var active = Guid.NewGuid();
            var retired = Guid.NewGuid();
            var staged = Guid.NewGuid();
            await using (var sql = await OpenAsync(database))
            {
                // Legacy bug reproduction: active/retired rows still carry a staged 24h expiry.
                await SeedVaultVersionAsync(sql, merchantId, active, version: 1, state: 2, hasExpiry: true);
                await SeedVaultVersionAsync(sql, merchantId, retired, version: 2, state: 3, hasExpiry: true);
                await SeedVaultVersionAsync(sql, merchantId, staged, version: 3, state: 1, hasExpiry: true);
            }

            await migrator.MigrateAsync(); // applies LegacyVaultExpiryRemediation

            await using (var verify = await OpenAsync(database))
            {
                Assert.True(await ExpiryIsNullAsync(verify, active));
                Assert.True(await ExpiryIsNullAsync(verify, retired));
                Assert.False(await ExpiryIsNullAsync(verify, staged)); // staged keeps its expiry
            }

            // Rollback window: Down is a no-op and drops nothing; the rows are still readable and Up re-applies
            // idempotently (AC-9.6).
            await migrator.MigrateAsync(PreviousMigration);
            await using (var afterDown = await OpenAsync(database))
                Assert.True(await ExpiryIsNullAsync(afterDown, active)); // one-way repair not reverted
            await migrator.MigrateAsync();
        }
        finally
        {
            await PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    // --- helpers ---

    private static LegacyPaymentRemediationService Service(
        ControlPlaneDbContext db, CommerceDbContext commerce,
        out FakeRouteSelector route, out FakeAdapterFactory adapters)
    {
        route = new FakeRouteSelector(null);
        adapters = new FakeAdapterFactory();
        return ServiceWith(db, commerce, route, adapters);
    }

    private static LegacyPaymentRemediationService ServiceWith(
        ControlPlaneDbContext db, CommerceDbContext commerce,
        IPaymentRouteSelector route, IPspAdapterFactory adapters)
    {
        var uow = new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance);
        var keyring = new VaultKeyring("k1", new Dictionary<string, byte[]> { ["k1"] = MasterKey });
        var vault = new LocalEnvelopeVaultStore(db, new FixedClock(), keyring, new NoopAuditWriter());
        return new LegacyPaymentRemediationService(
            db, commerce, uow, new PaymentAuthorizationSqlLockManager(db), vault, adapters, route, new FixedClock());
    }

    private static ControlPlaneDbContext ControlPlaneContext(string database, Guid merchantId) => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
        new Actor(merchantId), AllowWrites.Instance, NoOpSecurityTelemetry.Instance);

    private static CommerceDbContext CommerceContext(string database, Guid merchantId) => new(
        new DbContextOptionsBuilder<CommerceDbContext>()
            .UseSqlServer(IntegrationDb.SaConnFor(database), sql => sql.UseCompatibilityLevel(170)).Options,
        new Actor(merchantId), AllowWrites.Instance, NoOpSecurityTelemetry.Instance);

    private static Task<SqlConnection> OpenAsync(string database) =>
        IntegrationDb.OpenAsync(IntegrationDb.SaConnFor(database));

    private static async Task<int> EnvironmentOfAsync(SqlConnection sql, Guid merchantId) =>
        Convert.ToInt32(await IntegrationDb.ScalarAsync(sql,
            $"SELECT PaymentEnvironment FROM merch.Merchants WHERE Id = '{merchantId}';"));

    private static async Task<int> LegacyCountAsync(SqlConnection sql, Guid merchantId) =>
        Convert.ToInt32(await IntegrationDb.ScalarAsync(sql,
            $"SELECT COUNT(*) FROM txn.PaymentSessions WHERE MerchantId = '{merchantId}' AND RoutingSnapshotVersion = 0;"));

    private static async Task<Guid> PinnedConnectionAsync(SqlConnection sql, Guid merchantId) =>
        Guid.Parse(Convert.ToString(await IntegrationDb.ScalarAsync(sql,
            $"SELECT TOP 1 PspConnectionId FROM txn.PaymentSessions WHERE MerchantId = '{merchantId}' AND RoutingSnapshotVersion = 1;"))!);

    private static async Task<Guid?> PinnedSecretVersionAsync(SqlConnection sql, Guid merchantId)
    {
        var value = await IntegrationDb.ScalarAsync(sql,
            $"SELECT TOP 1 SecretVersionId FROM txn.PaymentSessions WHERE MerchantId = '{merchantId}' AND RoutingSnapshotVersion = 1;");
        return value is null or DBNull ? null : Guid.Parse(Convert.ToString(value)!);
    }

    private static async Task<bool> ExpiryIsNullAsync(SqlConnection sql, Guid versionId)
    {
        var value = await IntegrationDb.ScalarAsync(sql,
            $"SELECT ExpiresAt FROM merch.VaultSecretVersions WHERE Id = '{versionId}';");
        return value is null or DBNull;
    }

    private static Task SeedMerchantAsync(SqlConnection sql, Guid merchantId, int environment, bool switched) =>
        IntegrationDb.ExecAsync(sql, $"""
            DECLARE @created datetime2 = SYSUTCDATETIME();
            INSERT merch.Merchants
                (Id, Code, Name, Status, Country, Currency, EnabledChannels, CreatedAt, Version, Metadata,
                 PaymentEnvironment, PaymentEnvironmentUpdatedAt)
            VALUES (@id, @code, N'Legacy Merchant', 1, N'TH', N'THB', N'card', @created, 1, @meta,
                    {environment}, {(switched ? "DATEADD(hour, 1, @created)" : "@created")});
            """, ("@id", merchantId), ("@code", $"leg-{merchantId:N}"[..24]), ("@meta", "{}"));

    private static Task SeedConnectionAsync(SqlConnection sql, Guid merchantId, Guid connectionId, Code psp) =>
        IntegrationDb.ExecAsync(sql, $"""
            INSERT txn.PspConnections
                (Id, MerchantId, Psp, PaymentProviderId, EnabledMethods, SecretRefName,
                 IsEnabled, CreatedAt, Health, Version, Metadata, ActiveSecretEnvironment)
            VALUES (@id, @merchant, {(int)psp}, NULL, N'card', N'psp-connection-{connectionId:N}', 1,
                    SYSUTCDATETIME(), 1, 1, @meta, 2);
            """, ("@id", connectionId), ("@merchant", merchantId), ("@meta", "{}"));

    private static Task SeedLegacyV0SessionAsync(SqlConnection sql, Guid merchantId, Guid sessionId, string? chargeId) =>
        IntegrationDb.ExecAsync(sql, $"""
            INSERT txn.PaymentSessions
                (Id, MerchantId, OrderId, Method, Psp, Status, PspExternalChargeId,
                 CreatedAt, UpdatedAt, AmountAmount, AmountCurrency, RoutingSnapshotVersion, Version)
            VALUES (@id, @merchant, @order, N'card', 1, 1, {(chargeId is null ? "NULL" : "@charge")},
                    SYSUTCDATETIME(), SYSUTCDATETIME(), 100, 'THB', 0, 1);
            """, ("@id", sessionId), ("@merchant", merchantId), ("@order", Guid.NewGuid()),
            ("@charge", (object?)chargeId ?? DBNull.Value));

    private static Task SeedVaultVersionAsync(
        SqlConnection sql, Guid merchantId, Guid versionId, int version, int state, bool hasExpiry) =>
        IntegrationDb.ExecAsync(sql, $"""
            INSERT merch.VaultSecretVersions
                (Id, MerchantId, SecretName, Version, SecretKey, EncryptedDek, EncryptedSecret, Hint, State,
                 CreatedAt, ExpiresAt)
            VALUES (@id, @merchant, @name, {version}, N'k1', 0x00, 0x00, N'hint', {state},
                    SYSUTCDATETIME(), {(hasExpiry ? "DATEADD(hour, 24, SYSUTCDATETIME())" : "NULL")});
            """, ("@id", versionId), ("@merchant", merchantId), ("@name", $"psp-vault-{versionId:N}"));

    private sealed class FakeRouteSelector(PspRouteSelection? selection) : IPaymentRouteSelector
    {
        public Task<PspRouteSelection> SelectAsync(
            Guid merchantId, Guid orderId, string method, CancellationToken cancellationToken) =>
            selection is { } s
                ? Task.FromResult(s)
                : throw new ConflictException("No route.", "routing_unavailable");
    }

    private sealed class FakeAdapterFactory(IPspAdapter? adapter = null) : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => adapter ?? throw new NotSupportedException();
    }

    private sealed class FakeChargeAdapter(Func<string, bool> confirms, Money amount) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>();
        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, PspEnvironment environment, CancellationToken ct) =>
            throw new NotSupportedException();
        public bool VerifyWebhook(string rawPayload, string signature, string secret) => throw new NotSupportedException();
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, PspEnvironment environment, CancellationToken ct) =>
            confirms(secret)
                ? Task.FromResult(new PspChargeConfirmation(PspChargeStatus.Paid, amount))
                : throw new InvalidOperationException("The PSP rejected the secret for this charge.");
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }

    private sealed class Actor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId { get; } = merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => new(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
    }

    private sealed class AllowWrites : IWriteAuthorizer
    {
        public static readonly AllowWrites Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class NoopAuditWriter : IVaultRevealAuditWriter
    {
        public Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
