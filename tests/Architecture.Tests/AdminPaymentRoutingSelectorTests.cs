using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Domain.Items;
using Payments.Application.Ports;
using Payments.Domain;
using Payments.Domain.Psp;
using Payments.Domain.Routing;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Payments;
using SharedKernel;

namespace Architecture.Tests;

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
    public async Task Pre_bind_admin_selection_reads_explicit_merchant_order_ruleset_and_connection()
    {
        var amount = Money.Of(100m, "THB");
        var order = Order.Create(MerchantId, amount, Now,
            [new OrderItemInput(1, amount, "product", "variant", "Plan")], "ORD6900000001");
        var defaultConnection = Connection.Create(
            MerchantId, Code.TwoCTwoP, PaymentMethods.Card, "default-secret", Now);
        var routedConnection = Connection.Create(
            MerchantId, Code.Omise, PaymentMethods.Card, "routed-secret", Now);
        var ruleset = RoutingRuleset.Create(MerchantId, "active",
            [new RoutingRuleSpec(1, PaymentMethods.Card, null, null, null, routedConnection.Id, null, true)], Now);
        ruleset.RequestActivation(Guid.NewGuid(), Now.AddMinutes(1));
        ruleset.Activate(Now.AddMinutes(2));

        await using (var writer = NewContext(FakeActorContext.For(MerchantId)))
        {
            writer.AddRange(order, defaultConnection, routedConnection, ruleset);
            await writer.SaveChangesAsync();
        }

        await using var unbound = NewContext(FakeActorContext.Unbound);
        var selector = new AdminPaymentSessionReader(
            unbound, new DefaultPspSelection(Code.TwoCTwoP), new AdapterFactory());

        var selected = await selector.SelectAsync(MerchantId, order.Id, PaymentMethods.Card, default);

        Assert.Equal(Code.Omise, selected);
    }

    private MerchantRuntimeDbContext NewContext(IActorContext actor) => new(
        new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
        actor, FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    public void Dispose() => _connection.Dispose();

    private sealed class AdapterFactory : IPspAdapterFactory
    {
        public IPspAdapter For(Code psp) => new Adapter(psp);
    }

    private sealed class Adapter(Code psp) : IPspAdapter
    {
        public Code Psp => psp;
        public IReadOnlySet<string> SupportedMethods { get; } = new HashSet<string> { PaymentMethods.Card };
        public Task<PspCharge> CreateRedirectChargeAsync(
            Session session, Guid pspConnectionId, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public bool VerifyWebhook(string rawPayload, string signature, string secret) => false;
        public Task<PspChargeConfirmation> FetchChargeAsync(
            string externalChargeId, string secret, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public WebhookEvent ParseWebhook(string rawPayload) => throw new NotSupportedException();
    }
}
