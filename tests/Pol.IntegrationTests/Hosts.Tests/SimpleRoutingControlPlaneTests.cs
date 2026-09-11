extern alias ApiHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Persistence.ControlPlane.Payments;


using System.Security.Cryptography;
using System.Text.Json;
using ApiHost::Api.ControlPlane;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Payments.Application.AdminControlPlane;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Capabilities;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Payments.Infrastructure.Psp;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using Persistence.ControlPlane.Vault;
using Persistence.ControlPlane;
using Persistence.ControlPlane.Admins;
using SharedKernel;

namespace Hosts.Tests;

/// <summary>
/// merchant-psp-settings task 5 — the simple routing control plane at the store boundary, over the REAL
/// runtime context (SQLite), the REAL envelope vault and a recording adapter. Proves the property that no
/// simple-routing PUT can mutate or mint an advanced-predicate ruleset (adversarial cases #1-9), plus
/// server-side validation (AC-5.3), stale-ETag protection (AC-5.5) and activation coverage (AC-5.4). The
/// exact coded exceptions are what the shared ProblemDetailsExceptionHandler turns into the 400/409 wire
/// codes named in the spec; the HTTP wiring (permission/CSRF/If-Match/Idempotency-Key/OpenAPI) is pinned by
/// <see cref="OpenApi_pins_the_simple_routing_get_and_put_contracts"/>, PermissionGateSitesTests and the
/// header contract tests.
/// </summary>
public sealed class SimpleRoutingControlPlaneTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("a1000000-0000-4000-8000-0000000000a1");
    private static readonly Guid OtherMerchantId = Guid.Parse("a1000000-0000-4000-8000-0000000000a2");
    private static readonly Guid ActorId = Guid.Parse("a2000000-0000-4000-8000-0000000000a1");
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AdminPaymentsAccess Unrestricted = new(ActorId, 0, true, new HashSet<Guid>());
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly CommerceDbContext _commerce;
    private readonly VaultKeyring _keyring = new(VaultOptions.LegacyKeyId,
        new Dictionary<string, byte[]>(StringComparer.Ordinal) { [VaultOptions.LegacyKeyId] = RandomNumberGenerator.GetBytes(32) });

    public SimpleRoutingControlPlaneTests()
    {
        _connection.Open();
        using var setup = NewContext();
        _commerce = NewCommerceContext();
        RuntimeSchema.EnsureCreated(setup, _commerce, _connection);
        setup.Merchants.Add(Merchant.CreateWithId(MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [], "{}", Now));
        setup.Merchants.Add(Merchant.CreateWithId(OtherMerchantId, "vsouvenir", "Other", null, "TH", "THB", [], "{}", Now));
        setup.SaveChanges();
    }

    // ---- AC-5.2 / AC-5.5: create, read back, replace, stale-ETag (#6) ----

    [Fact]
    public async Task Put_creates_a_simple_draft_that_carries_no_advanced_predicate_and_read_back_matches()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);

        var before = await store.GetSimpleRoutingAsync(MerchantId, Unrestricted, default);
        Assert.NotNull(before);
        Assert.Null(before.RulesetId);
        Assert.False(before.AdvancedReadOnly);
        Assert.Empty(before.Rules);
        Assert.Equal(0, before.Version);                                   // AC-5.5 no-draft sentinel

        var created = await store.SetSimpleRoutingAsync(new SetSimpleRoutingIntent(
            MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-1", Unrestricted), default);

        Assert.NotNull(created.RulesetId);
        Assert.False(created.AdvancedReadOnly);
        Assert.Equal(1, created.Version);
        var row = Assert.Single(created.Rules);
        Assert.Equal(PaymentMethods.Card, row.Method);
        Assert.Equal(connectionId, row.PrimaryConnectionId);
        Assert.Null(row.FallbackConnectionId);

        // #5: the persisted rule carries no amount / Originator / any predicate, whatever a client might send.
        var persisted = await db.RoutingRulesets.IgnoreQueryFilters().Include(x => x.Rules).SingleAsync();
        var rule = Assert.Single(persisted.Rules);
        Assert.Null(rule.MinAmount);
        Assert.Null(rule.MaxAmount);
        Assert.Null(rule.OriginatorId);
        Assert.NotEqual("any", rule.Method);

        var after = await store.GetSimpleRoutingAsync(MerchantId, Unrestricted, default);
        Assert.Equal(created.RulesetId, after!.RulesetId);
        Assert.Equal(1, after.Version);
        Assert.Equal(PaymentMethods.Card, Assert.Single(after.Rules).Method);
    }

    [Fact]
    [Trait("Capability", "MerchantConfiguration")]
    [Trait("Requirement", "REQ-5.9")]
    public async Task Put_with_a_stale_etag_after_a_prior_put_is_a_state_conflict_and_writes_nothing()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);

        await store.SetSimpleRoutingAsync(new SetSimpleRoutingIntent(
            MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-a", Unrestricted), default);   // version -> 1

        // #6: another writer's ETag (0) is stale now.
        var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-b", Unrestricted), default));
        Assert.Equal("state_conflict", conflict.Code);

        var replaced = await store.SetSimpleRoutingAsync(new SetSimpleRoutingIntent(
            MerchantId, [Row(PaymentMethods.Card, connectionId)], 1, "put-c", Unrestricted), default);   // fresh ETag ok
        Assert.Equal(2, replaced.Version);
    }

    // ---- #7: idempotency ----

    [Fact]
    public async Task Reusing_an_idempotency_key_replays_the_same_intent_and_rejects_a_different_one()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);

        var first = await store.SetSimpleRoutingAsync(new SetSimpleRoutingIntent(
            MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "key-1", Unrestricted), default);
        var replay = await store.SetSimpleRoutingAsync(new SetSimpleRoutingIntent(
            MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "key-1", Unrestricted), default);
        Assert.Equal(first.RulesetId, replay.RulesetId);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal(1, await db.RoutingRulesets.IgnoreQueryFilters().CountAsync());   // replay did no second write

        // #7: same key, a different intent (fallback added) -> 409 idempotency_key_reused.
        var reused = await Assert.ThrowsAsync<ConflictException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId, Guid.NewGuid())], 0, "key-1", Unrestricted), default));
        Assert.Equal("idempotency_key_reused", reused.Code);
    }

    // ---- #1-#4: advanced-rule read-only guard ----

    public static TheoryData<string> AdvancedPredicates => new() { "amount", "originator", "any" };

    [Theory]
    [MemberData(nameof(AdvancedPredicates))]
    public async Task Put_over_an_advanced_draft_is_read_only_and_leaves_the_draft_untouched(string predicate)
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);
        var advancedId = await SeedAdvancedDraftAsync(db, connectionId, predicate);   // #1 amount / #2 originator / #3 any

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId)], 1, "put-adv", Unrestricted), default));
        Assert.Equal("advanced_routing_read_only", conflict.Code);

        var draft = await db.RoutingRulesets.IgnoreQueryFilters().Include(x => x.Rules).SingleAsync(x => x.Id == advancedId);
        Assert.Equal(1, draft.Version);                                    // untouched
        Assert.Contains(draft.Rules, IsAdvanced);
        Assert.Equal(0, await db.OperationRecords.CountAsync(x => x.Operation == "routing.simple-set"));
        var read = await store.GetSimpleRoutingAsync(MerchantId, Unrestricted, default);
        Assert.True(read!.AdvancedReadOnly);
        Assert.Empty(read.Rules);
    }

    [Fact]
    public async Task Put_when_an_advanced_active_ruleset_exists_and_no_draft_is_read_only_and_creates_no_draft()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);
        await SeedAdvancedActiveAsync(db, connectionId);                   // #4: advanced ACTIVE, no draft

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-adv-active", Unrestricted), default));
        Assert.Equal("advanced_routing_read_only", conflict.Code);
        Assert.Equal(0, await db.RoutingRulesets.IgnoreQueryFilters().CountAsync(x => x.Status == RoutingRulesetStatus.Draft));
    }

    [Fact]
    public async Task Put_when_an_advanced_ruleset_is_pending_approval_is_read_only_and_creates_no_draft()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);
        // An advanced ruleset already sent for activation (PendingApproval) — not Draft, not Active.
        var ruleset = RoutingRuleset.Create(
            MerchantId, "advanced-pending",
            [new RoutingRuleSpec(1, PaymentMethods.Card, null, 100m, 200m, connectionId, null, true)], Now);
        ruleset.RequestActivation(Guid.CreateVersion7(), Now);
        db.RoutingRulesets.Add(ruleset);
        await db.SaveChangesAsync();

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-pending", Unrestricted), default));
        Assert.Equal("advanced_routing_read_only", conflict.Code);
        Assert.Equal(0, await db.RoutingRulesets.IgnoreQueryFilters().CountAsync(x => x.Status == RoutingRulesetStatus.Draft));
        Assert.True((await store.GetSimpleRoutingAsync(MerchantId, Unrestricted, default))!.AdvancedReadOnly);
    }

    // ---- #8 / #9: server-side validation ----

    [Fact]
    public async Task Primary_equal_to_fallback_is_validation_failed()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId, connectionId)], 0, "put-eq", Unrestricted), default));
        Assert.Equal("validation_failed", error.Code);                    // #8
        Assert.Equal(0, await db.RoutingRulesets.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    [Trait("Capability", "MerchantConfiguration")]
    [Trait("Requirement", "REQ-5.9")]
    public async Task Primary_in_another_merchant_or_the_wrong_environment_is_validation_failed()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);

        // #9a: a connection that belongs to another merchant is unknown for this merchant.
        var foreign = (await store.CreateConnectionAsync(new CreatePspConnectionIntent(
            OtherMerchantId, "2c2p", [PaymentMethods.Card], null,
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0002" }, "MERCHANT-002", "create-foreign",
            Unrestricted), default)).Connection.PspConnectionId;
        var crossMerchant = await Assert.ThrowsAsync<InvalidRequestException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, foreign)], 0, "put-x1", Unrestricted), default));
        Assert.Equal("validation_failed", crossMerchant.Code);

        // #9b: the merchant moves to live while the connection credential stays sandbox.
        var merchant = await db.Merchants.IgnoreQueryFilters().SingleAsync(x => x.Id == MerchantId);
        merchant.StagePaymentEnvironment(PspEnvironment.Live, Guid.NewGuid());
        merchant.ActivatePendingPaymentEnvironment(Now);
        await db.SaveChangesAsync();
        var envMismatch = await Assert.ThrowsAsync<InvalidRequestException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-x2", Unrestricted), default));
        Assert.Equal("validation_failed", envMismatch.Code);
    }

    [Fact]
    [Trait("Capability", "MerchantConfiguration")]
    [Trait("Requirement", "REQ-5.9")]
    public async Task A_method_disabled_at_the_merchant_policy_level_is_validation_failed()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db, enableMerchantPolicy: false);   // AC-5.3 merchant side

        var error = await Assert.ThrowsAsync<InvalidRequestException>(() => store.SetSimpleRoutingAsync(
            new SetSimpleRoutingIntent(MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-no-policy", Unrestricted), default));
        Assert.Equal("validation_failed", error.Code);
    }

    // ---- #5: an extra amount/originator field on the request record is dropped, never a predicate ----

    [Fact]
    public void Simple_routing_row_request_ignores_amount_and_originator_fields()
    {
        var row = JsonSerializer.Deserialize<SimpleRoutingRowRequest>("""
            {"method":"card","primaryConnectionId":"a1000000-0000-4000-8000-0000000000ff",
             "fallbackConnectionId":null,"amount":"100.00","minAmount":"1.00","originatorId":"a1000000-0000-4000-8000-0000000000ee",
             "predicates":[{"any":true}]}
            """, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var members = typeof(SimpleRoutingRowRequest).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(members.SetEquals(["Method", "PrimaryConnectionId", "FallbackConnectionId"]));
        Assert.Equal(PaymentMethods.Card, row.Method);
        Assert.Equal(Guid.Parse("a1000000-0000-4000-8000-0000000000ff"), row.PrimaryConnectionId);
        Assert.Null(row.FallbackConnectionId);

        // The Application-layer row the store consumes has no predicate surface at all.
        var storeRowMembers = typeof(SimpleRoutingRuleRow).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("Amount", storeRowMembers);
        Assert.DoesNotContain("MinAmount", storeRowMembers);
        Assert.DoesNotContain("OriginatorId", storeRowMembers);
    }

    // ---- AC-5.4: activation coverage over enabled merchant methods ----

    [Fact]
    [Trait("Capability", "MerchantConfiguration")]
    [Trait("Requirement", "REQ-5.9")]
    public async Task Activation_is_routing_incomplete_when_an_enabled_merchant_method_has_no_primary()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);
        // PromptPay enabled at the merchant policy level but the draft only routes card.
        db.MerchantPaymentMethods.Add(MerchantPaymentMethod.Create(MerchantId, PaymentCapabilityIds.PromptPay, true, ActorId, Now));
        await db.SaveChangesAsync();
        var draft = await store.SetSimpleRoutingAsync(new SetSimpleRoutingIntent(
            MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-cov", Unrestricted), default);

        var conflict = await Assert.ThrowsAsync<ConflictException>(() => store.RequestActivationAsync(
            new RequestRoutingActivationIntent(draft.RulesetId!.Value, MerchantId, draft.Version, "act-1", "corr-1", Unrestricted), default));
        Assert.Equal("routing_incomplete", conflict.Code);
    }

    [Fact]
    [Trait("Capability", "MerchantConfiguration")]
    [Trait("Requirement", "REQ-5.9")]
    public async Task Activation_succeeds_when_every_enabled_merchant_method_has_a_primary()
    {
        await using var db = NewContext();
        var store = Store(db, new RecordingAdapterFactory());
        var connectionId = await SeedCardAsync(store, db);
        var draft = await store.SetSimpleRoutingAsync(new SetSimpleRoutingIntent(
            MerchantId, [Row(PaymentMethods.Card, connectionId)], 0, "put-ok", Unrestricted), default);

        var activation = await store.RequestActivationAsync(new RequestRoutingActivationIntent(
            draft.RulesetId!.Value, MerchantId, draft.Version, "act-ok", "corr-ok", Unrestricted), default);
        Assert.NotEqual(Guid.Empty, activation.ApprovalId);
        Assert.Equal("pending", activation.Ruleset.Status);
    }

    // ---- OpenAPI contract ----

    [Fact]
    public async Task OpenApi_pins_the_simple_routing_get_and_put_contracts()
    {
        using var factory = new SimpleRoutingFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/openapi/v1.json");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var paths = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("paths");
        var path = paths.GetProperty("/api/v1/payments/merchant-settings/{merchantId}/simple-routing");

        var get = path.GetProperty("get");
        Assert.Equal("GetMerchantSimpleRouting", get.GetProperty("operationId").GetString());
        Assert.True(get.GetProperty("responses").GetProperty("200").GetProperty("headers").TryGetProperty("ETag", out _));

        var put = path.GetProperty("put");
        Assert.Equal("SetMerchantSimpleRouting", put.GetProperty("operationId").GetString());
        AssertRequiredHeader(put, "If-Match");
        AssertRequiredHeader(put, "Idempotency-Key");
    }

    private static void AssertRequiredHeader(JsonElement operation, string name)
    {
        var header = operation.GetProperty("parameters").EnumerateArray().Single(x =>
            x.GetProperty("in").GetString() == "header"
            && string.Equals(x.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
        Assert.True(header.GetProperty("required").GetBoolean());
    }

    // ---- helpers ----

    private static SimpleRoutingRuleRow Row(string method, Guid primary, Guid? fallback = null) =>
        new(method, primary, fallback);

    private static bool IsAdvanced(RoutingRule rule) =>
        rule.Method == "any" || rule.OriginatorId is not null
        || rule.MinAmount is not null || rule.MaxAmount is not null;

    private async Task<Guid> SeedCardAsync(
        AdminPaymentsControlStore store, ControlPlaneDbContext db, bool enableMerchantPolicy = true)
    {
        var connectionId = (await store.CreateConnectionAsync(new CreatePspConnectionIntent(
            MerchantId, "2c2p", [PaymentMethods.Card], null,
            new Dictionary<string, string> { ["secretKey"] = "2c2p-secret-key-0001" }, "MERCHANT-001", "create-card",
            Unrestricted), default)).Connection.PspConnectionId;
        if (enableMerchantPolicy)
        {
            db.MerchantPaymentMethods.Add(MerchantPaymentMethod.Create(MerchantId, PaymentCapabilityIds.Card, true, ActorId, Now));
            await db.SaveChangesAsync();
        }
        return connectionId;
    }

    private async Task<Guid> SeedAdvancedDraftAsync(ControlPlaneDbContext db, Guid connectionId, string predicate)
    {
        var spec = predicate switch
        {
            "amount" => new RoutingRuleSpec(1, PaymentMethods.Card, null, 100m, 200m, connectionId, null, true),
            "originator" => new RoutingRuleSpec(1, PaymentMethods.Card, await SeedOriginatorAsync(db), null, null, connectionId, null, true),
            "any" => new RoutingRuleSpec(1, "any", null, null, null, connectionId, null, true),
            _ => throw new ArgumentOutOfRangeException(nameof(predicate)),
        };
        var ruleset = RoutingRuleset.Create(MerchantId, "advanced", [spec], Now);
        db.RoutingRulesets.Add(ruleset);
        await db.SaveChangesAsync();
        return ruleset.Id;
    }

    private async Task SeedAdvancedActiveAsync(ControlPlaneDbContext db, Guid connectionId)
    {
        var ruleset = RoutingRuleset.Create(
            MerchantId, "advanced-active",
            [new RoutingRuleSpec(1, PaymentMethods.Card, null, 100m, 200m, connectionId, null, true)], Now);
        ruleset.RequestActivation(Guid.CreateVersion7(), Now);
        ruleset.Activate(Now);
        db.RoutingRulesets.Add(ruleset);
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedOriginatorAsync(ControlPlaneDbContext db)
    {
        var originator = Originator.Create(MerchantId, "BR1", "Branch", OriginatorType.Branch, null, null, Now);
        db.Set<Originator>().Add(originator);
        await db.SaveChangesAsync();
        return originator.Id;
    }

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
        public Guid MerchantId => SimpleRoutingControlPlaneTests.MerchantId;
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
        public IReadOnlySet<string> SupportedMethods { get; } =
            new HashSet<string> { PaymentMethods.Card, PaymentMethods.PromptPay };

        public Task<PspProbeResult> TestConnectionAsync(string secret, PspEnvironment environment, CancellationToken ct) =>
            Task.FromResult(new PspProbeResult("authenticated", "ok"));
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

file sealed class SimpleRoutingFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
    }
}
