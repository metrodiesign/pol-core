using BuildingBlocks.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Domain;
using Orders.Domain.Items;
using Persistence.MerchantRuntime;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;

namespace Architecture.Tests;

/// <summary>
/// Proves the per-agent read floor on <see cref="MerchantRuntimeDbContext"/>: a bound merchant user (Tier 1
/// agent/broker) reads only the orders it initiated — its own customers — while a merchant-bound scope with no
/// user (admin ambient binding, webhook, worker) keeps the merchant-wide read and the cross-merchant floor of
/// <see cref="ReadFloorTests"/> still holds. The user predicate lives in the same query filter as the merchant
/// predicate, so every merchant-user read path (list, by-id, reconciliation, payment-session mint) is covered
/// by one seam rather than per-handler checks.
/// </summary>
public sealed class OrderVisibilityFloorTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AgentOne = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgentTwo = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Originator = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private readonly SqliteConnection _connection;

    public OrderVisibilityFloorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = NewContext(FakeActorContext.Unbound);
        setup.Database.EnsureCreated();
    }

    private MerchantRuntimeDbContext NewContext(IActorContext actor) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options, actor,
            FakeWriteAuthorizer.AllowAll, NoOpSecurityTelemetry.Instance);

    [Fact]
    public async Task Merchant_user_reads_only_the_orders_it_initiated()
    {
        var own = await SeedAgentOrderAsync(MerchantA, AgentOne, "ORD6900000001");
        await SeedAgentOrderAsync(MerchantA, AgentTwo, "ORD6900000002");
        await SeedAdminOrderAsync(MerchantA, "ORD6900000003");

        using var asAgentOne = NewContext(FakeActorContext.For(MerchantA, AgentOne));
        var visible = await asAgentOne.Orders.Select(o => o.Id).ToListAsync();

        Assert.Equal([own], visible);
    }

    [Fact]
    public async Task By_id_load_of_another_agents_order_in_the_same_merchant_is_IDOR_closed()
    {
        var otherAgentsOrder = await SeedAgentOrderAsync(MerchantA, AgentTwo, "ORD6900000002");

        using var asAgentOne = NewContext(FakeActorContext.For(MerchantA, AgentOne));
        var found = await asAgentOne.Orders.FirstOrDefaultAsync(o => o.Id == otherAgentsOrder);

        Assert.Null(found);
    }

    [Fact]
    public async Task Merchant_bound_scope_without_a_user_keeps_the_merchant_wide_read()
    {
        await SeedAgentOrderAsync(MerchantA, AgentOne, "ORD6900000001");
        await SeedAgentOrderAsync(MerchantA, AgentTwo, "ORD6900000002");
        await SeedAdminOrderAsync(MerchantA, "ORD6900000003");
        await SeedAgentOrderAsync(MerchantB, AgentOne, "ORD6900000004");

        using var asMerchantA = NewContext(FakeActorContext.For(MerchantA));
        using var asMerchantB = NewContext(FakeActorContext.For(MerchantB));
        using var unbound = NewContext(FakeActorContext.Unbound);

        Assert.Equal(3, await asMerchantA.Orders.CountAsync());
        Assert.Equal(1, await asMerchantB.Orders.CountAsync());
        Assert.Equal(0, await unbound.Orders.CountAsync());
    }

    [Fact]
    public async Task Same_agent_id_in_another_merchant_never_crosses_the_merchant_floor()
    {
        await SeedAgentOrderAsync(MerchantB, AgentOne, "ORD6900000004");

        using var asAgentOneInA = NewContext(FakeActorContext.For(MerchantA, AgentOne));

        Assert.Empty(await asAgentOneInA.Orders.ToListAsync());
    }

    [Fact]
    public void Generated_SQL_binds_the_user_predicate_to_the_instance_member()
    {
        using var asAgentOne = NewContext(FakeActorContext.For(MerchantA, AgentOne));
        var sql = asAgentOne.Orders.ToQueryString();
        var whereLine = sql.Split('\n').Single(l => l.Contains("WHERE", StringComparison.Ordinal));

        Assert.Contains("@ef_filter__CurrentMerchant", whereLine);
        Assert.Contains("@ef_filter__CurrentMerchantUser", whereLine);
        Assert.DoesNotContain(AgentOne.ToString(), whereLine, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Guid> SeedAgentOrderAsync(Guid merchantId, Guid agentId, string orderNo)
    {
        var order = OrderAggregate.Create(merchantId, Money.Of(15000m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(1, Money.Of(15000m, "THB"), "DOC-1", "VMI", "ประกันรถยนต์")], orderNo,
            saleCode: "77001", initiatingAudience: OrderInitiatingAudience.User, initiatingMerchantUserId: agentId);
        return await SeedAsync(merchantId, order);
    }

    private async Task<Guid> SeedAdminOrderAsync(Guid merchantId, string orderNo)
    {
        var order = OrderAggregate.Create(merchantId, Money.Of(15000m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(1, Money.Of(15000m, "THB"), "DOC-1", "VMI", "ประกันรถยนต์")], orderNo,
            saleCode: "77001", originatorId: Originator, initiatingAudience: OrderInitiatingAudience.PlatformAdmin);
        return await SeedAsync(merchantId, order);
    }

    private async Task<Guid> SeedAsync(Guid merchantId, OrderAggregate order)
    {
        using var writer = NewContext(FakeActorContext.For(merchantId));
        writer.Add(order);
        await writer.SaveChangesAsync();
        return order.Id;
    }

    public void Dispose() => _connection.Dispose();
}
