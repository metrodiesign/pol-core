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
using Persistence.MerchantRuntime.Vault;
using SharedKernel;

namespace Hosts.Tests;

/// <summary>
/// merchant-psp-settings task 2 — the connection control plane end to end at the store boundary, over the
/// REAL runtime context (SQLite), the REAL envelope vault store and the REAL envelope factory; only the PSP
/// adapter is a recorder. Proves: environment inheritance (REQ-2.1/2.2), one record per provider with a named
/// 409 and both providers side by side (REQ-3.1/3.2/3.7), zero-method create enabled + unknown health
/// (REQ-3.3/3.4), field allowlist / size / Omise prefix rejected BEFORE any vault write (REQ-4.7-4.11), the
/// safe view (REQ-4.5/4.6/11.1), the environment-aware active test (REQ-7.1) and the 404-not-403 merchant
/// settings read (REQ-1.4).
/// </summary>
public sealed class AdminPspConnectionControlPlaneTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("a1000000-0000-4000-8000-000000000011");
    private static readonly Guid OtherMerchantId = Guid.Parse("a1000000-0000-4000-8000-000000000012");
    private static readonly Guid ActorId = Guid.Parse("a2000000-0000-4000-8000-000000000011");
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AdminPaymentsAccess Unrestricted = new(ActorId, 0, true, new HashSet<Guid>());
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly VaultKeyring _keyring = new(VaultOptions.LegacyKeyId,
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { [VaultOptions.LegacyKeyId] = RandomNumberGenerator.GetBytes(32) });

    public AdminPspConnectionControlPlaneTests()
    {
        _connection.Open();
        using var setup = NewContext();
        setup.Database.EnsureCreated();
        setup.Merchants.Add(Merchant.CreateWithId(MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [], "{}", Now));
        setup.Merchants.Add(Merchant.CreateWithId(OtherMerchantId, "vsouvenir", "Other", null, "TH", "THB", [], "{}", Now));
        setup.SaveChanges();
    }

    [Fact]
    public async Task Zero_method_connection_inherits_merchant_environment_and_returns_a_safe_view()
    {
        await using var db = NewContext();
        var adapters = new RecordingAdapterFactory();
        var store = Store(db, adapters);

        var result = await store.CreateConnectionAsync(Intent("2c2p", [],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-9f3a" }, "MERCHANT-001", "create-1"), default);

        var view = result.Connection;
        Assert.False(result.Replayed);
        Assert.True(view.IsEnabled);                                   // REQ-3.3
        Assert.Equal("unknown", view.Health);                          // REQ-3.4
        Assert.Empty(view.EnabledMethods);
        Assert.Equal("sandbox", view.Environment);                     // REQ-2.1/2.2
        Assert.Equal("sandbox", view.CredentialEnvironment);
        Assert.Equal($"https://api.test/api/v1/webhooks/{view.PspConnectionId:D}", view.CallbackUrl); // REQ-11.1
        Assert.Null(view.PendingCredentialTest);
        Assert.False(view.WebhookRegistration.Acknowledged);
        Assert.Equal("****9f3a", view.MaskedSecrets["secretKey"]);      // REQ-4.5
        Assert.DoesNotContain("2c2p-secret-key-9f3a", System.Text.Json.JsonSerializer.Serialize(view)); // REQ-4.6

        var row = await db.PspConnections.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(PspEnvironment.Sandbox, row.ActiveSecretEnvironment);
        Assert.Equal(string.Empty, row.EnabledMethods);
        Assert.NotNull(row.ActiveSecretVersionId);
        var operation = await db.AdminOperationRecords.SingleAsync();
        Assert.Equal(201, operation.HttpStatus);
        Assert.DoesNotContain("2c2p-secret-key-9f3a", operation.Result);  // REQ-9.10
    }

    [Fact]
    public async Task Second_connection_for_the_same_provider_is_a_named_409_but_another_provider_is_allowed()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create-a"), default);

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.CreateConnectionAsync(Intent("2c2p", [],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" }, "MERCHANT-001", "create-b"), default));
        Assert.Equal("psp_connection_exists", conflict.Code);          // REQ-3.7

        var omise = await store.CreateConnectionAsync(Intent("omise", [],
            new Dictionary<string, string> { ["secretKey"] = "skey_test_abcdef" }, null, "create-c"), default);
        Assert.Equal("omise", omise.Connection.Psp);                   // REQ-3.2
        Assert.Equal(2, await db.PspConnections.IgnoreQueryFilters().CountAsync());
    }

    public static TheoryData<string, IReadOnlyDictionary<string, string>, string?> RejectedSecretInputs => new()
    {
        { "2c2p", new Dictionary<string, string> { ["secretKey"] = "k", ["apiKey"] = "x" }, "M" },        // REQ-4.10 unknown field
        { "2c2p", new Dictionary<string, string> { ["secretKey"] = new string('k', 4_097) }, "M" },        // REQ-4.11 size
        { "2c2p", new Dictionary<string, string> { ["secretKey"] = "k" }, null },                          // REQ-4.1 merchantId required
        { "omise", new Dictionary<string, string> { ["publicKey"] = "pkey_test_x" }, null },               // REQ-4.2 secretKey required
        { "omise", new Dictionary<string, string> { ["secretKey"] = "skey_live_xyz" }, null },             // REQ-4.8 live key on sandbox merchant
    };

    [Theory]
    [MemberData(nameof(RejectedSecretInputs))]
    public async Task Invalid_credential_fields_are_400_before_any_vault_write(
        string psp, IReadOnlyDictionary<string, string> secrets, string? pspMerchantId)
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() =>
            store.CreateConnectionAsync(Intent(psp, [], secrets, pspMerchantId, "create-bad"), default));

        Assert.Equal("validation_failed", error.Code);                 // REQ-4.7
        Assert.Equal(0, await db.VaultSecretVersions.IgnoreQueryFilters().CountAsync());
        Assert.Equal(0, await db.PspConnections.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Active_test_probes_the_provider_in_the_connection_environment()
    {
        await using var db = NewContext();
        var adapters = new RecordingAdapterFactory();
        var store = Store(db, adapters);
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create-t"), default)).Connection;

        var tested = await store.TestConnectionAsync(new TestPspConnectionIntent(
            created.PspConnectionId, MerchantId, created.Version, "test-1", Unrestricted), default);

        Assert.Equal(PspEnvironment.Sandbox, adapters.Adapter.ProbedEnvironment);   // REQ-7.1
        Assert.Contains("2c2p-secret-key-0001", adapters.Adapter.ProbedSecret);       // decrypted server-side only
        Assert.Equal("healthy", tested.Connection.Health);
        Assert.Equal("authenticated", tested.Connection.LastTestResult);
    }

    [Fact]
    public async Task Merchant_settings_read_is_404_outside_scope_and_carries_the_merchant_version()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());

        var visible = await store.GetMerchantPaymentSettingsAsync(MerchantId, Unrestricted, default);
        Assert.NotNull(visible);
        Assert.Equal("sandbox", visible.Environment);
        Assert.Null(visible.PendingEnvironment);
        Assert.Equal(1, visible.Version);

        var scoped = new AdminPaymentsAccess(ActorId, 0, false, new HashSet<Guid> { OtherMerchantId });
        Assert.Null(await store.GetMerchantPaymentSettingsAsync(MerchantId, scoped, default));   // REQ-1.4
        Assert.Null(await store.GetMerchantPaymentSettingsAsync(Guid.NewGuid(), Unrestricted, default));
    }

    private static CreatePspConnectionIntent Intent(
        string psp, IReadOnlyList<string> methods, IReadOnlyDictionary<string, string> secrets,
        string? pspMerchantId, string key) =>
        new(MerchantId, psp, methods, null, secrets, pspMerchantId, key, Unrestricted);

    private AdminPaymentsControlStore Store(MerchantRuntimeDbContext db, IPspAdapterFactory adapters) => new(
        db,
        new FixedClock(),
        new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        new LocalEnvelopeVaultStore(db, new FixedClock(), _keyring, new NoopAuditWriter()),
        new PspSecretEnvelopeFactory(),
        adapters,
        AllowLease.Instance);

    private MerchantRuntimeDbContext NewContext() => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        new Actor(), new AllowAllWrites(), NoOpSecurityTelemetry.Instance);

    public void Dispose() => _connection.Dispose();

    private sealed class Actor : IActorContext
    {
        public Guid MerchantId => AdminPspConnectionControlPlaneTests.MerchantId;
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
        public RecordingAdapter Adapter { get; } = new();
        public IPspAdapter For(Code psp) => Adapter;
    }

    private sealed class RecordingAdapter : IPspAdapter
    {
        public PspEnvironment? ProbedEnvironment { get; private set; }
        public string? ProbedSecret { get; private set; }
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string> { PaymentMethods.Card };

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken ct)
        {
            ProbedEnvironment = environment;
            ProbedSecret = secret;
            return Task.FromResult(new PspProbeResult("authenticated", "ok"));
        }

        public string CallbackUrlFor(Guid pspConnectionId) => $"https://api.test/api/v1/webhooks/{pspConnectionId:D}";

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
