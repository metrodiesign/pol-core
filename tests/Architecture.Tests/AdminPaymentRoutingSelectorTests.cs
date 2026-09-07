using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure.Vault;
using Merchants.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Domain.Items;
using Payments.Application.Capabilities;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using SharedKernel;

namespace Architecture.Tests;

/// <summary>
/// The server-side route selector (merchant-psp-settings REQ-6.8-6.18): it resolves a connection from the
/// merchant's active ruleset, tries primary before fallback, refuses when nothing is eligible (no deployment
/// default — REQ-6.18), and returns the pinned snapshot (connection, secret version, environment). Local
/// eligibility layers on top of the shared capability resolver (REQ-5.15), which is faked here — its real
/// two-level join runs SQL-Server-only SQL and is proven in the integration suite. What THIS test drives is
/// the routing logic around it: primary/fallback order, environment match, credential reference, refusal.
/// </summary>
public sealed class AdminPaymentRoutingSelectorTests : IDisposable
{
    private static readonly Guid MerchantId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public AdminPaymentRoutingSelectorTests()
    {
        _connection.Open();
        using var setup = NewContext(FakeActorContext.For(MerchantId));
        setup.Database.EnsureCreated();
    }

    [Fact]
    public async Task Selects_the_primary_connection_and_returns_its_pinned_snapshot()
    {
        var order = NewOrder();
        var primary = EligibleConnection(Code.TwoCTwoP, out var primaryVersion);
        var ruleset = ActiveRuleset(new RoutingRuleSpec(
            1, PaymentMethods.Card, null, null, null, primary.Id, null, true));
        await Seed(order, [primary], ruleset);

        var selection = await Select(order, PaymentMethods.Card,
            qualifying: new() { ["2c2p"] = primary.Id });

        Assert.Equal(primary.Id, selection.PspConnectionId);
        Assert.Equal(Code.TwoCTwoP, selection.Psp);
        Assert.Equal(primaryVersion, selection.SecretVersionId);
        Assert.Equal(PspEnvironment.Sandbox, selection.Environment);
    }

    [Fact]
    public async Task Falls_back_when_the_primary_is_not_eligible()
    {
        // AC-4.4: the primary fails local eligibility (the capability resolver denies it) before any PSP call,
        // so the fallback is chosen. Fallback is used ONLY here, before the charge — never after.
        var order = NewOrder();
        var primary = EligibleConnection(Code.TwoCTwoP, out _);
        var fallback = EligibleConnection(Code.Omise, out var fallbackVersion);
        var ruleset = ActiveRuleset(new RoutingRuleSpec(
            1, PaymentMethods.Card, null, null, null, primary.Id, fallback.Id, true));
        await Seed(order, [primary, fallback], ruleset);

        var selection = await Select(order, PaymentMethods.Card,
            qualifying: new() { ["omise"] = fallback.Id }); // 2c2p not in the map => denied

        Assert.Equal(fallback.Id, selection.PspConnectionId);
        Assert.Equal(Code.Omise, selection.Psp);
        Assert.Equal(fallbackVersion, selection.SecretVersionId);
    }

    [Fact]
    public async Task Refuses_when_there_is_no_active_ruleset()
    {
        // REQ-6.18: no active routing covering the method is a refusal, never a deployment default.
        var order = NewOrder();
        await Seed(order, [EligibleConnection(Code.TwoCTwoP, out _)], ruleset: null);

        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await Select(order, PaymentMethods.Card, qualifying: []));

        Assert.Equal("routing_unavailable", ex.Code);
    }

    [Fact]
    public async Task Refuses_when_no_candidate_is_eligible()
    {
        var order = NewOrder();
        var primary = EligibleConnection(Code.TwoCTwoP, out _);
        var ruleset = ActiveRuleset(new RoutingRuleSpec(
            1, PaymentMethods.Card, null, null, null, primary.Id, null, true));
        await Seed(order, [primary], ruleset);

        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await Select(order, PaymentMethods.Card, qualifying: [])); // capability resolver denies everything

        Assert.Equal("routing_unavailable", ex.Code);
    }

    [Fact]
    public async Task Skips_a_connection_whose_environment_differs_from_the_merchant()
    {
        // REQ-6.14: eligibility requires the active credential's environment to match the merchant's. The
        // merchant is Sandbox (the default); a Live connection is skipped even though the capability resolver
        // would allow it, so with no other candidate the route is refused.
        var order = NewOrder();
        var live = EligibleConnection(Code.TwoCTwoP, out _, environment: PspEnvironment.Live);
        var ruleset = ActiveRuleset(new RoutingRuleSpec(
            1, PaymentMethods.Card, null, null, null, live.Id, null, true));
        await Seed(order, [live], ruleset);

        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await Select(order, PaymentMethods.Card, qualifying: new() { ["2c2p"] = live.Id }));

        Assert.Equal("routing_unavailable", ex.Code);
    }

    [Fact]
    public async Task Skips_a_connection_without_an_active_secret_version()
    {
        // REQ-6.14: a connection with no credential reference cannot be routed to, even when the capability
        // resolver allows the method.
        var order = NewOrder();
        var noSecret = Connection.Create(MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "ref", Now);
        var ruleset = ActiveRuleset(new RoutingRuleSpec(
            1, PaymentMethods.Card, null, null, null, noSecret.Id, null, true));
        await Seed(order, [noSecret], ruleset);

        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await Select(order, PaymentMethods.Card, qualifying: new() { ["2c2p"] = noSecret.Id }));

        Assert.Equal("routing_unavailable", ex.Code);
    }

    [Fact]
    public async Task Skips_a_disabled_connection()
    {
        // REQ-3.5: a disabled connection is never selected for a new attempt.
        var order = NewOrder();
        var disabled = EligibleConnection(Code.TwoCTwoP, out _);
        typeof(Connection).GetProperty(nameof(Connection.IsEnabled))!.SetValue(disabled, false);
        var ruleset = ActiveRuleset(new RoutingRuleSpec(
            1, PaymentMethods.Card, null, null, null, disabled.Id, null, true));
        await Seed(order, [disabled], ruleset);

        var ex = await Assert.ThrowsAsync<ConflictException>(async () =>
            await Select(order, PaymentMethods.Card, qualifying: new() { ["2c2p"] = disabled.Id }));

        Assert.Equal("routing_unavailable", ex.Code);
    }

    private static Order NewOrder()
    {
        var amount = Money.Of(100m, "THB");
        return Order.Create(MerchantId, amount, Now,
            [new OrderItemInput(1, amount, "product", "variant", "Plan")], "ORD6900000001");
    }

    private static Connection EligibleConnection(
        Code psp, out Guid secretVersionId, PspEnvironment environment = PspEnvironment.Sandbox)
    {
        var connection = Connection.Create(
            MerchantId, psp, PaymentMethods.Card, $"secret/{psp}", Now, environment: environment);
        secretVersionId = Guid.NewGuid();
        connection.SetInitialSecretVersion(secretVersionId, environment);
        return connection;
    }

    private static RoutingRuleset ActiveRuleset(RoutingRuleSpec rule)
    {
        var ruleset = RoutingRuleset.Create(MerchantId, "active", [rule], Now);
        ruleset.RequestActivation(Guid.NewGuid(), Now.AddMinutes(1));
        ruleset.Activate(Now.AddMinutes(2));
        return ruleset;
    }

    private async Task Seed(Order order, Connection[] connections, RoutingRuleset? ruleset)
    {
        await using var writer = NewContext(FakeActorContext.For(MerchantId));
        writer.Merchants.Add(Merchant.CreateWithId(
            MerchantId, "vcommerce", "Merchant", null, "TH", "THB", [PaymentMethods.Card], "{}", Now));
        writer.Add(order);
        writer.AddRange(connections);
        // A connection's ActiveSecretVersionId is a composite FK into merch.VaultSecretVersions — seed a row
        // so the connection persists (the selector never decrypts it; only its existence matters here).
        foreach (var connection in connections)
        {
            if (connection.ActiveSecretVersionId is { } versionId)
                writer.VaultSecretVersions.Add(new VaultSecretVersion(
                    versionId, MerchantId, connection.SecretRefName, 1, "key-1", [1], [1], "hint", Now, null));
        }
        if (ruleset is not null)
            writer.Add(ruleset);
        await writer.SaveChangesAsync();
    }

    private async Task<PspRouteSelection> Select(
        Order order, string method, Dictionary<string, Guid> qualifying)
    {
        await using var unbound = NewContext(FakeActorContext.Unbound);
        var selector = new AdminPaymentSessionReader(unbound, new FakeCapabilities(qualifying));
        return await selector.SelectAsync(MerchantId, order.Id, method, default);
    }

    private MerchantRuntimeDbContext NewContext(IActorContext actor) => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        actor, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    public void Dispose() => _connection.Dispose();

    /// <summary>Stands in for the shared capability resolver: a connection qualifies iff its provider code
    /// maps to its own id here, which lets a test drive the primary-denied-then-fallback branch without the
    /// SQL-Server-only policy join (REQ-5.15, proven in the integration suite).</summary>
    private sealed class FakeCapabilities(Dictionary<string, Guid> qualifying)
        : IEffectivePaymentCapabilityResolver
    {
        public Task<PaymentMethodDecision> ResolveMethodAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) =>
            Task.FromResult(request.ProviderCode is { } code && qualifying.TryGetValue(code, out var id)
                ? new PaymentMethodDecision(true, request.Method, PaymentCapabilityDenial.None, id)
                : new PaymentMethodDecision(false, request.Method, PaymentCapabilityDenial.AccountUnavailable, null));

        public Task<IReadOnlyList<EffectivePaymentMethod>> ListMethodsAsync(
            PaymentCapabilitySubject subject, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentMethod>>([]);

        public Task<IReadOnlyList<EffectivePaymentOption>> ResolveOptionsAsync(
            ResolvePaymentMethod request, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EffectivePaymentOption>>([]);
    }
}
