using System.Security.Cryptography;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Infrastructure.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using Persistence.ControlPlane.Vault;
using SharedKernel;
using Persistence.ControlPlane.Payments;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;

namespace Hosts.Tests;

/// <summary>
/// merchant-psp-settings task 7 — the atomic environment-switch REQUEST at the store boundary, over the REAL
/// runtime context (SQLite), the REAL envelope vault store and the REAL envelope factory. Proves the request
/// stages a candidate for EVERY connection under one approval (REQ-2.11), and the guards that refuse a switch:
/// missing credential (REQ-2.16 -> environment_credentials_incomplete), unknown environment (REQ-2.6),
/// Omise-live without the callback acknowledgement (REQ-11.2 -> webhook_not_ready), an already-pending
/// approval (approval_pending), and an unresolved legacy snapshot (critical #12 -> legacy_snapshot_blocked).
/// Activation itself is exercised in <see cref="Architecture.Tests.AdminPaymentsApprovalExecutorTests"/>.
/// </summary>
[Trait("Capability", "MerchantConfiguration")]
[Trait("Requirement", "REQ-5.3")]
[Trait("Requirement", "REQ-5.5")]
[Trait("Requirement", "REQ-5.6")]
public sealed class MerchantEnvironmentControlPlaneTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("c1000000-0000-4000-8000-000000000011");
    private static readonly Guid ActorId = Guid.Parse("c2000000-0000-4000-8000-000000000011");
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AdminPaymentsAccess Unrestricted = new(ActorId, 0, true, new HashSet<Guid>());
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CommerceDbContext _commerce;
    private readonly VaultKeyring _keyring = new(VaultOptions.LegacyKeyId,
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { [VaultOptions.LegacyKeyId] = RandomNumberGenerator.GetBytes(32) });

    public MerchantEnvironmentControlPlaneTests()
    {
        _connection.Open();
        using var setup = NewContext();
        _commerce = NewCommerceContext();
        RuntimeSchema.EnsureCreated(setup, _commerce, _connection);
        setup.Merchants.Add(Merchant.CreateWithId(MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [], "{}", Now));
        setup.SaveChanges();
    }

    [Fact]
    public async Task Requesting_a_live_switch_stages_every_connection_and_returns_pending()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);

        var result = await store.RequestEnvironmentChangeAsync(LiveSwitch(
            version, "switch-1", omiseAck: true, LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")), default);

        Assert.False(result.Replayed);
        Assert.Equal("pending", result.Status);
        Assert.Equal("live", result.TargetEnvironment);
        Assert.Equal(2, result.ConnectionCount);                                       // REQ-2.11 every connection
        Assert.NotEqual(Guid.Empty, result.ApprovalId);

        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Live, merchant.PendingPaymentEnvironment);         // staged, not yet active
        Assert.Equal(PspEnvironment.Sandbox, merchant.PaymentEnvironment);
        Assert.Equal(result.ApprovalId, merchant.PendingPaymentEnvironmentApprovalId);

        foreach (var id in new[] { twoCTwoP, omise })
        {
            var row = await db.PspConnections.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
            Assert.Equal(result.ApprovalId, row.PendingApprovalId);                    // one approval owns all
            Assert.Equal(PspEnvironment.Live, row.PendingSecretEnvironment);
            Assert.NotNull(row.PendingSecretVersionId);
            Assert.Equal(PspEnvironment.Sandbox, row.ActiveSecretEnvironment);         // active untouched until approve
        }
        // Omise callback acknowledgement recorded (REQ-11.2), 2c2p not required to.
        var omiseRow = await db.PspConnections.IgnoreQueryFilters().SingleAsync(x => x.Id == omise);
        Assert.NotNull(omiseRow.WebhookRegistrationHash);
        var operation = await db.OperationRecords.SingleAsync(x => x.Operation == "payment.environment-change");
        Assert.Equal(202, operation.ResponseStatus);
        Assert.DoesNotContain("skey_live", operation.ResponseBody);                     // REQ-9.10 no secret in ledger
    }

    [Fact]
    public async Task A_switch_missing_a_connection_credential_is_environment_credentials_incomplete()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, _, version) = await SeedTwoSandboxConnectionsAsync(store);

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestEnvironmentChangeAsync(
            LiveSwitch(version, "switch-missing", omiseAck: true, LiveCredential(twoCTwoP, "2c2p")), default));

        Assert.Equal("environment_credentials_incomplete", conflict.Code);             // REQ-2.16 no partial switch
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        Assert.Null(merchant.PendingPaymentEnvironment);                               // nothing staged
        Assert.Equal(2, await db.VaultSecretVersions.IgnoreQueryFilters().CountAsync()); // only the two active
    }

    [Fact]
    public async Task An_unknown_target_environment_is_validation_failed()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() => store.RequestEnvironmentChangeAsync(
            new RequestEnvironmentChangeIntent(MerchantId, "production", true,
                [LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")],
                version, "switch-bad-env", "corr", Unrestricted), default));

        Assert.Equal("validation_failed", error.Code);                                 // REQ-2.6
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        Assert.Null(merchant.PendingPaymentEnvironment);
    }

    [Fact]
    public async Task A_live_switch_without_the_omise_callback_acknowledgement_is_webhook_not_ready()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestEnvironmentChangeAsync(
            LiveSwitch(version, "switch-no-ack", omiseAck: false,
                LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")), default));

        Assert.Equal("webhook_not_ready", conflict.Code);                              // REQ-11.2
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        Assert.Null(merchant.PendingPaymentEnvironment);
    }

    [Fact]
    public async Task A_switch_while_an_environment_change_is_pending_is_approval_pending()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);
        await store.RequestEnvironmentChangeAsync(LiveSwitch(
            version, "switch-first", omiseAck: true, LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")), default);
        var refreshed = (await store.GetMerchantPaymentSettingsAsync(MerchantId, Unrestricted, default))!;

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestEnvironmentChangeAsync(
            LiveSwitch(refreshed.Version, "switch-second", omiseAck: true,
                LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")), default));

        Assert.Equal("approval_pending", conflict.Code);
    }

    [Fact]
    public async Task A_switch_while_a_connection_credential_change_is_pending_is_approval_pending()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);
        var conn = (await store.GetConnectionAsync(twoCTwoP, MerchantId, Unrestricted, default))!;
        await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
            twoCTwoP, MerchantId, new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-rot" },
            "MERCHANT-2C2P", conn.Version, "rotate", "corr", Unrestricted), default);
        var refreshed = (await store.GetMerchantPaymentSettingsAsync(MerchantId, Unrestricted, default))!;

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestEnvironmentChangeAsync(
            LiveSwitch(refreshed.Version, "switch-during-rotation", omiseAck: true,
                LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")), default));

        Assert.Equal("approval_pending", conflict.Code);
    }

    [Fact]
    public async Task A_switch_blocked_by_an_unresolved_legacy_session_is_legacy_snapshot_blocked()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);
        await SeedLegacyV0SessionAsync();

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestEnvironmentChangeAsync(
            LiveSwitch(version, "switch-legacy", omiseAck: true,
                LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")), default));

        Assert.Equal("legacy_snapshot_blocked", conflict.Code);                        // critical #12
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        Assert.Null(merchant.PendingPaymentEnvironment);
    }

    [Fact]
    public async Task A_switch_is_not_blocked_by_a_legacy_v0_session_without_an_external_charge()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);
        // A snapshot-version-0 session that never took a charge carries no unproven money (AC-9.2): it is
        // upgraded to v1 by remediation, not a block — the switch must proceed, not 409 legacy_snapshot_blocked.
        await SeedLegacyV0SessionAsync(charged: false);

        var result = await store.RequestEnvironmentChangeAsync(LiveSwitch(
            version, "switch-uncharged", omiseAck: true,
            LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise")), default);

        Assert.Equal("pending", result.Status);
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        Assert.Equal(PspEnvironment.Live, merchant.PendingPaymentEnvironment);
    }

    [Fact]
    public async Task A_connection_outside_the_merchant_is_validation_failed()
    {
        await using var db = NewContext();
        var store = Store(db);
        var (twoCTwoP, omise, version) = await SeedTwoSandboxConnectionsAsync(store);

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() => store.RequestEnvironmentChangeAsync(
            new RequestEnvironmentChangeIntent(MerchantId, "live", true,
                [LiveCredential(twoCTwoP, "2c2p"), LiveCredential(omise, "omise"),
                 LiveCredential(Guid.NewGuid(), "2c2p")],
                version, "switch-stranger", "corr", Unrestricted), default));

        Assert.Equal("validation_failed", error.Code);
    }

    private async Task<(Guid TwoCTwoP, Guid Omise, long MerchantVersion)> SeedTwoSandboxConnectionsAsync(
        AdminPaymentsControlStore store)
    {
        var twoCTwoP = (await store.CreateConnectionAsync(new CreatePspConnectionIntent(
            MerchantId, "2c2p", [], null, new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" },
            "MERCHANT-2C2P", "create-2c2p", Unrestricted), default)).Connection;
        var omise = (await store.CreateConnectionAsync(new CreatePspConnectionIntent(
            MerchantId, "omise", [], null, new Dictionary<string, string> { ["secretKey"] = "skey_test_0001" },
            null, "create-omise", Unrestricted), default)).Connection;
        var settings = (await store.GetMerchantPaymentSettingsAsync(MerchantId, Unrestricted, default))!;
        return (twoCTwoP.PspConnectionId, omise.PspConnectionId, settings.Version);
    }

    private static EnvironmentChangeConnectionCredential LiveCredential(Guid connectionId, string psp) =>
        psp == "omise"
            ? new EnvironmentChangeConnectionCredential(
                connectionId, new Dictionary<string, string> { ["secretKey"] = "skey_live_0001" }, null)
            : new EnvironmentChangeConnectionCredential(
                connectionId, new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-live" }, "MERCHANT-2C2P");

    private static RequestEnvironmentChangeIntent LiveSwitch(
        long expectedVersion, string key, bool omiseAck, params EnvironmentChangeConnectionCredential[] connections) =>
        new(MerchantId, "live", omiseAck, connections, expectedVersion, key, "corr", Unrestricted);

    // A legacy snapshot-version-0 session is never minted by Session.Create (and its rowversion column is not
    // insertable through EF on SQLite), so it is written with raw SQL to reproduce a pre-existing legacy row.
    private async Task SeedLegacyV0SessionAsync(bool charged = true)
    {
        var table = _commerce.Model.FindEntityType(typeof(Session))!.GetTableName();
        var sql = "INSERT INTO \"" + table + "\" "
            + "(\"Id\", \"MerchantId\", \"OrderId\", \"Method\", \"Psp\", \"Status\", \"PspExternalChargeId\", "
            + "\"CreatedAt\", \"UpdatedAt\", \"RowVersion\", \"AmountAmount\", \"AmountCurrency\", "
            + "\"RoutingSnapshotVersion\", \"Version\") "
            + "VALUES ({0}, {1}, {2}, 'card', 1, 1, " + (charged ? "'chrg_legacy'" : "NULL") + ", {3}, {3}, {4}, 100, 'THB', 0, 1)";
        await _commerce.Database.ExecuteSqlRawAsync(sql, Guid.NewGuid(), MerchantId, Guid.NewGuid(), Now, new byte[8]);
    }

    private AdminPaymentsControlStore Store(ControlPlaneDbContext db) => new(
        db,
        _commerce,
        new FixedClock(),
        new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        new LocalEnvelopeVaultStore(db, new FixedClock(), _keyring, new NoopAuditWriter()),
        new PspSecretEnvelopeFactory(),
        new RecordingAdapterFactory(),
        AllowLease.Instance);

    private ControlPlaneDbContext NewContext() => new(
        new DbContextOptionsBuilder<ControlPlaneDbContext>().UseSqlite(_connection).Options,
        new Actor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    private CommerceDbContext NewCommerceContext() => new(
        new DbContextOptionsBuilder<CommerceDbContext>().UseSqlite(_connection).Options,
        new Actor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    public void Dispose()
    {
        _commerce.Dispose();
        _connection.Dispose();
    }

    private sealed class Actor : IActorContext
    {
        public Guid MerchantId => MerchantEnvironmentControlPlaneTests.MerchantId;
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

    private sealed class NoopAuditWriter : IVaultRevealAuditWriter
    {
        public Task AppendAsync(Guid merchantId, string secretName, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AllowLease : IMerchantRuntimeAuthorizationLease
    {
        public static readonly AllowLease Instance = new();
        public Task VerifyAsync(AdminPaymentsAccess access, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingAdapterFactory : IPspAdapterFactory
    {
        private readonly RecordingAdapter _adapter = new();
        public IPspAdapter For(Code requested) => _adapter;
    }

    private sealed class RecordingAdapter : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string>();
        public string CallbackUrlFor(Guid pspConnectionId) => $"https://api.test/api/v1/webhooks/{pspConnectionId:D}";

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken ct) =>
            Task.FromResult(new PspProbeResult("authenticated", "ok"));
        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, PspEnvironment environment, CancellationToken ct) =>
            throw new NotSupportedException();
        public bool VerifyWebhook(string rawPayload, string signature, string secret) => throw new NotSupportedException();
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, PspEnvironment environment, CancellationToken ct) =>
            throw new NotSupportedException();
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }
}
