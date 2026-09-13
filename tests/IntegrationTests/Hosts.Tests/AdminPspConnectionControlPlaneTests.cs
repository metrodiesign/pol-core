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
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using SharedKernel;
using Persistence.ControlPlane.Payments;

namespace Hosts.Tests;

/// <summary>
/// merchant-psp-settings tasks 2-3 — the connection control plane end to end at the store boundary, over the
/// REAL runtime context (SQLite), the REAL envelope vault store and the REAL envelope factory; only the PSP
/// adapter is a recorder. Proves: environment inheritance (REQ-2.1/2.2), one record per provider with a named
/// 409 and both providers side by side (REQ-3.1/3.2/3.7), zero-method create enabled + unknown health
/// (REQ-3.3/3.4), field allowlist / size / Omise prefix rejected BEFORE any vault write (REQ-4.7-4.11), the
/// safe view (REQ-4.5/4.6/11.1), the environment-aware active test (REQ-7.1) and the 404-not-403 merchant
/// settings read (REQ-1.4).
/// </summary>
[Trait("Capability", "MerchantConfiguration")]
[Trait("Requirement", "REQ-5.3")]
[Trait("Requirement", "REQ-5.6")]
public sealed class AdminPspConnectionControlPlaneTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("a1000000-0000-4000-8000-000000000011");
    private static readonly Guid OtherMerchantId = Guid.Parse("a1000000-0000-4000-8000-000000000012");
    private static readonly Guid ActorId = Guid.Parse("a2000000-0000-4000-8000-000000000011");
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AdminPaymentsAccess Unrestricted = new(ActorId, 0, true, new HashSet<Guid>());
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CommerceDbContext _commerce;
    private readonly VaultKeyring _keyring = new(VaultOptions.LegacyKeyId,
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { [VaultOptions.LegacyKeyId] = RandomNumberGenerator.GetBytes(32) });

    public AdminPspConnectionControlPlaneTests()
    {
        _connection.Open();
        using var setup = NewContext();
        _commerce = NewCommerceContext();
        RuntimeSchema.EnsureCreated(setup, _commerce, _connection);
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
        var operation = await db.OperationRecords.SingleAsync();
        Assert.Equal(201, operation.ResponseStatus);
        Assert.DoesNotContain("2c2p-secret-key-9f3a", operation.ResponseBody);  // REQ-9.10
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

    [Fact]
    public async Task Omise_methods_are_listed_but_unavailable_and_cannot_be_enabled_until_verified()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory(Code.Omise));
        var created = (await store.CreateConnectionAsync(Intent("omise", [],
            new Dictionary<string, string> { ["secretKey"] = "skey_test_abcdef" }, null, "create-o"), default)).Connection;

        // REQ-5.3/5.16: all three canonical methods are shown; REQ-5.11: none is available without evidence.
        Assert.Equal([PaymentMethods.Card, PaymentMethods.PromptPay, PaymentMethods.Installment],
            created.Methods.Select(x => x.Method));
        Assert.All(created.Methods, x => Assert.False(x.Available));
        Assert.All(created.Methods, x => Assert.False(x.AdapterVerified));
        Assert.Equal("adapter_unverified", created.Methods.Single(x => x.Method == PaymentMethods.Card).Denial);
        Assert.Equal("provider_method_unavailable", created.Methods.Single(x => x.Method == PaymentMethods.PromptPay).Denial);

        // REQ-5.5: enabling fails closed, and the credential-only connection is still manageable.
        await Assert.ThrowsAsync<PaymentCapabilityUnavailableException>(() => store.SetAccountMethodAsync(
            new SetAccountPaymentCapabilityIntent(created.PspConnectionId, PaymentMethods.Card, null, true, 0, "enable-o", Unrestricted), default));
        var tested = await store.TestConnectionAsync(new TestPspConnectionIntent(
            created.PspConnectionId, MerchantId, created.Version, "test-o", Unrestricted), default);
        Assert.Equal("healthy", tested.Connection.Health);
    }

    [Fact]
    public async Task Account_method_switch_is_reported_per_method_without_touching_merchant_policy()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create-m"), default)).Connection;

        var card = created.Methods.Single(x => x.Method == PaymentMethods.Card);
        Assert.True(card.AccountEnabled);
        Assert.True(card.AdapterVerified);
        Assert.True(card.Available);
        Assert.Null(card.Denial);
        var promptPay = created.Methods.Single(x => x.Method == PaymentMethods.PromptPay);
        Assert.False(promptPay.AccountEnabled);
        Assert.True(promptPay.AdapterVerified);
        Assert.Equal("account_method_disabled", promptPay.Denial);

        var view = await store.GetAccountMethodAsync(created.PspConnectionId, PaymentMethods.PromptPay, Unrestricted, default);
        Assert.NotNull(view);
        Assert.True(view.AdapterVerified);
        Assert.Equal("account_method_disabled", view.Denial);

        // REQ-5.14: the merchant-level policy table is untouched by account-level changes.
        Assert.Equal(0, await db.MerchantPaymentMethods.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Routing_draft_requires_normalized_account_method_and_matching_credential_environment()
    {
        await using var db = NewContext();
        var adapters = new RecordingAdapterFactory();
        var store = Store(db, adapters);
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create-r"), default)).Connection;
        var connectionId = created.PspConnectionId;

        // REQ-6.6: promptpay has no enabled account row even though the adapter supports it.
        var noAccount = await Assert.ThrowsAsync<InvalidRequestException>(() => store.CreateRulesetAsync(
            new CreateRoutingRulesetIntent(MerchantId, "pp", [Rule(PaymentMethods.PromptPay, connectionId)], Unrestricted), default));
        Assert.Equal("routing_invalid", noAccount.Code);

        // REQ-5.8: widening the legacy CSV projection grants nothing — the normalized rows decide.
        var tracked = await db.PspConnections.IgnoreQueryFilters().SingleAsync(x => x.Id == connectionId);
        tracked.ProjectEnabledMethods([PaymentMethods.Card, PaymentMethods.PromptPay]);
        await db.SaveChangesAsync();
        var csvOnly = await Assert.ThrowsAsync<InvalidRequestException>(() => store.CreateRulesetAsync(
            new CreateRoutingRulesetIntent(MerchantId, "csv", [Rule(PaymentMethods.PromptPay, connectionId)], Unrestricted), default));
        Assert.Equal("routing_invalid", csvOnly.Code);

        // REQ-6.15: a failed health test does not block routing; the account row is what counts.
        tracked = await db.PspConnections.IgnoreQueryFilters().SingleAsync(x => x.Id == connectionId);
        tracked.RecordTest(false, "probe_failed", Now);
        await db.SaveChangesAsync();
        var ruleset = await store.CreateRulesetAsync(
            new CreateRoutingRulesetIntent(MerchantId, "card", [Rule(PaymentMethods.Card, connectionId)], Unrestricted), default);
        Assert.Equal("draft", ruleset.Status);
        Assert.Null(adapters.Adapter.ProbedSecret);  // REQ-6.16: eligibility never runs a live probe

        // The Control Plane UoW clears its tracker at each transaction boundary; reload the canonical owner
        // before each direct fixture mutation so the write is real and the following routing guard observes it.
        tracked = await db.PspConnections.IgnoreQueryFilters().SingleAsync(x => x.Id == connectionId);
        // REQ-6.14: a connection with no credential reference is not routable, even with the account row on.
        var activeSecretVersionId = tracked.ActiveSecretVersionId;
        db.Entry(tracked).Property(x => x.ActiveSecretVersionId).CurrentValue = null;
        await db.SaveChangesAsync();
        var noCredential = await Assert.ThrowsAsync<InvalidRequestException>(() => store.CreateRulesetAsync(
            new CreateRoutingRulesetIntent(MerchantId, "no-cred", [Rule(PaymentMethods.Card, connectionId)], Unrestricted), default));
        Assert.Equal("routing_invalid", noCredential.Code);
        db.Entry(tracked).Property(x => x.ActiveSecretVersionId).CurrentValue = activeSecretVersionId;
        await db.SaveChangesAsync();

        // REQ-6.7: the merchant moves to live while the active credential stays sandbox -> refused.
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        merchant.StagePaymentEnvironment(PspEnvironment.Live, Guid.NewGuid());
        merchant.ActivatePendingPaymentEnvironment(Now);
        await db.SaveChangesAsync();
        var mismatch = await Assert.ThrowsAsync<InvalidRequestException>(() => store.CreateRulesetAsync(
            new CreateRoutingRulesetIntent(MerchantId, "live", [Rule(PaymentMethods.Card, connectionId)], Unrestricted), default));
        Assert.Equal("routing_invalid", mismatch.Code);
    }

    [Fact]
    public async Task Candidate_test_probes_the_candidate_at_its_environment_without_touching_active_health()
    {
        await using var db = NewContext();
        var adapters = new RecordingAdapterFactory();
        var store = Store(db, adapters);
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create"), default)).Connection;

        // The active credential is Healthy before the candidate test.
        var tested = (await store.TestConnectionAsync(new TestPspConnectionIntent(
            created.PspConnectionId, MerchantId, created.Version, "active-test", Unrestricted), default)).Connection;
        Assert.Equal("healthy", tested.Health);

        var change = await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
            created.PspConnectionId, MerchantId,
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" }, "MERCHANT-001",
            tested.Version, "change", "corr", Unrestricted), default);
        var staged = await store.GetConnectionAsync(created.PspConnectionId, MerchantId, Unrestricted, default);

        var result = (await store.TestCandidateCredentialAsync(new TestPspCandidateCredentialIntent(
            created.PspConnectionId, MerchantId, change.ApprovalId, staged!.Version, "candidate-test", Unrestricted), default)).Connection;

        Assert.Equal("authenticated", result.PendingCredentialTest!.Result);   // AC-6.2 candidate result recorded
        Assert.Contains("0002", adapters.Adapter.ProbedSecret);                 // probed the CANDIDATE, not the active secret
        Assert.Equal(PspEnvironment.Sandbox, adapters.Adapter.ProbedEnvironment);
        Assert.Equal("healthy", result.Health);                                 // AC-6.2/REQ-7.10 active health untouched
        Assert.True(result.HasPendingCredentialChange);                          // candidate NOT activated
    }

    [Fact]
    public async Task Candidate_test_result_is_not_written_when_the_connection_changed_during_the_probe()
    {
        Guid connectionId;
        Guid approvalId;
        long stagedVersion;
        await using (var seed = NewContext())
        {
            var store = Store(seed, new RecordingAdapterFactory());
            var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
                new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create"), default)).Connection;
            connectionId = created.PspConnectionId;
            var change = await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
                connectionId, MerchantId, new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" },
                "MERCHANT-001", created.Version, "change", "corr", Unrestricted), default);
            approvalId = change.ApprovalId;
            stagedVersion = (await store.GetConnectionAsync(connectionId, MerchantId, Unrestricted, default))!.Version;
        }

        // A concurrent mutation lands DURING the probe (after the pre-probe version check passed): the in-transaction
        // compare-after-probe (critical #18) must discard the stale result rather than write it.
        var mutating = new ProbeCallbackAdapterFactory(() =>
        {
            using var concurrent = NewContext();
            var row = concurrent.PspConnections.IgnoreQueryFilters().Single(x => x.Id == connectionId);
            row.Update(row.EnabledMethods, row.Metadata, isEnabled: false);
            concurrent.SaveChanges();
        });
        await using var db = NewContext();
        var raced = Store(db, mutating);
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => raced.TestCandidateCredentialAsync(
            new TestPspCandidateCredentialIntent(connectionId, MerchantId, approvalId, stagedVersion, "candidate-test", Unrestricted), default));

        await using var verify = NewContext();
        var connection = await verify.PspConnections.IgnoreQueryFilters().SingleAsync(x => x.Id == connectionId);
        Assert.Null(connection.PendingSecretTestResult);   // no stale write
        Assert.NotNull(connection.PendingApprovalId);       // still pending
    }

    [Fact]
    public async Task Candidate_test_failure_is_502_and_records_probe_failed()
    {
        await using var db = NewContext();
        var store = Store(db, new ThrowingAdapterFactory());
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create"), default)).Connection;
        var change = await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
            created.PspConnectionId, MerchantId, new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" },
            "MERCHANT-001", created.Version, "change", "corr", Unrestricted), default);
        var staged = await store.GetConnectionAsync(created.PspConnectionId, MerchantId, Unrestricted, default);

        var failed = await Assert.ThrowsAsync<PspConnectionTestFailedException>(() => store.TestCandidateCredentialAsync(
            new TestPspCandidateCredentialIntent(created.PspConnectionId, MerchantId, change.ApprovalId, staged!.Version, "candidate-test", Unrestricted), default));
        Assert.Equal("probe_failed", failed.Connection.PendingCredentialTest!.Result);
        Assert.True(failed.Connection.HasPendingCredentialChange);   // failure does not reject the candidate (REQ-7.11)
    }

    [Fact]
    public async Task A_second_credential_change_while_one_is_pending_is_approval_pending()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create"), default)).Connection;
        await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
            created.PspConnectionId, MerchantId, new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" },
            "MERCHANT-001", created.Version, "change-1", "corr", Unrestricted), default);
        var staged = await store.GetConnectionAsync(created.PspConnectionId, MerchantId, Unrestricted, default);

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestCredentialChangeAsync(
            new RequestPspCredentialChangeIntent(created.PspConnectionId, MerchantId,
                new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0003" }, "MERCHANT-001",
                staged!.Version, "change-2", "corr", Unrestricted), default));
        Assert.Equal("approval_pending", conflict.Code);   // AC-6.1
    }

    [Fact]
    public async Task A_credential_change_while_an_environment_change_is_pending_is_approval_pending()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create"), default)).Connection;

        // A merchant-wide environment switch is pending (task 7 territory); a lone credential change must defer.
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        merchant.StagePaymentEnvironment(PspEnvironment.Live, Guid.NewGuid());
        await db.SaveChangesAsync();
        var refreshed = await store.GetConnectionAsync(created.PspConnectionId, MerchantId, Unrestricted, default);

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestCredentialChangeAsync(
            new RequestPspCredentialChangeIntent(created.PspConnectionId, MerchantId,
                new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" }, "MERCHANT-001",
                refreshed!.Version, "change", "corr", Unrestricted), default));
        Assert.Equal("approval_pending", conflict.Code);   // AC-7.6 coupling guard
    }

    [Fact]
    public async Task A_credential_change_while_a_legacy_session_is_unresolved_is_legacy_snapshot_blocked()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create"), default)).Connection;

        // The merchant still has a snapshot-version-0 Session with an external charge whose historical secret
        // has not been proven — task 9 remediation must clear it before a new credential can activate (AC-9.3,
        // the same block task 7 puts on an environment switch).
        await SeedLegacyV0SessionAsync();
        var refreshed = await store.GetConnectionAsync(created.PspConnectionId, MerchantId, Unrestricted, default);

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestCredentialChangeAsync(
            new RequestPspCredentialChangeIntent(created.PspConnectionId, MerchantId,
                new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" }, "MERCHANT-001",
                refreshed!.Version, "change", "corr", Unrestricted), default));
        Assert.Equal("legacy_snapshot_blocked", conflict.Code);
    }

    [Fact]
    public async Task A_credential_change_is_not_blocked_by_a_legacy_v0_session_without_an_external_charge()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var created = (await store.CreateConnectionAsync(Intent("2c2p", [PaymentMethods.Card],
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create"), default)).Connection;

        // A snapshot-version-0 Session that never took a charge carries no unproven money (AC-9.2): it is
        // upgraded to v1 by remediation, not a block — the credential change must proceed, not be 409'd.
        await SeedLegacyV0SessionAsync(charged: false);
        var refreshed = await store.GetConnectionAsync(created.PspConnectionId, MerchantId, Unrestricted, default);

        var change = await store.RequestCredentialChangeAsync(new RequestPspCredentialChangeIntent(
            created.PspConnectionId, MerchantId,
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" }, "MERCHANT-001",
            refreshed!.Version, "change", "corr", Unrestricted), default);

        Assert.NotEqual(Guid.Empty, change.ApprovalId);
    }

    // A snapshot-version-0 session is never minted by Session.Create (and its rowversion is not insertable
    // through EF on SQLite), so it is written with raw SQL to reproduce a pre-existing legacy row.
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

    private static RoutingRuleInput Rule(string method, Guid connectionId) =>
        new(1, method, null, null, null, connectionId, null, true);

    private static CreatePspConnectionIntent Intent(
        string psp, IReadOnlyList<string> methods, IReadOnlyDictionary<string, string> secrets,
        string? pspMerchantId, string key) =>
        new(MerchantId, psp, methods, null, secrets, pspMerchantId, key, Unrestricted);

    private AdminPaymentsControlStore Store(ControlPlaneDbContext db, IPspAdapterFactory adapters) => new(
        db,
        _commerce,
        new FixedClock(),
        new ControlPlaneUnitOfWork(db, NoOpSecurityTelemetry.Instance),
        new LocalEnvelopeVaultStore(db, new FixedClock(), _keyring, new NoopAuditWriter()),
        new PspSecretEnvelopeFactory(),
        adapters,
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

    private sealed class RecordingAdapterFactory(Code psp = Code.TwoCTwoP) : IPspAdapterFactory
    {
        public RecordingAdapter Adapter { get; } = new(psp);
        public IPspAdapter For(Code requested) => Adapter;
    }

    private sealed class ProbeCallbackAdapterFactory(Action onProbe) : IPspAdapterFactory
    {
        private readonly ProbeCallbackAdapter _adapter = new(onProbe);
        public IPspAdapter For(Code requested) => _adapter;
    }

    private sealed class ProbeCallbackAdapter(Action onProbe) : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string> { PaymentMethods.Card, PaymentMethods.PromptPay };

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken ct)
        {
            onProbe();
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

    private sealed class ThrowingAdapterFactory : IPspAdapterFactory
    {
        private readonly ThrowingAdapter _adapter = new();
        public IPspAdapter For(Code requested) => _adapter;
    }

    private sealed class ThrowingAdapter : IPspAdapter
    {
        public Code Psp => Code.TwoCTwoP;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string> { PaymentMethods.Card, PaymentMethods.PromptPay };

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken ct) =>
            throw new InvalidOperationException("probe failed");

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

    /// <summary>2C2P declares card + promptpay; Omise declares nothing — mirroring the real adapters'
    /// evidence state (REQ-5.11).</summary>
    private sealed class RecordingAdapter(Code psp) : IPspAdapter
    {
        public PspEnvironment? ProbedEnvironment { get; private set; }
        public string? ProbedSecret { get; private set; }
        public Code Psp => psp;
        public IReadOnlySet<string> SupportedMethods { get; } = psp == Code.TwoCTwoP
            ? new HashSet<string> { PaymentMethods.Card, PaymentMethods.PromptPay }
            : new HashSet<string>();

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
