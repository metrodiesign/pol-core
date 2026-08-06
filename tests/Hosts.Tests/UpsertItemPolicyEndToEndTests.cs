extern alias ApiHost;

using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Orders.Application;
using Orders.Domain.Items;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders.Items;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;

namespace Hosts.Tests;

/// <summary>
/// policy-reference-record task 4 — the merchant-plane write path (REQ-3.1/3.3/3.4/3.5/3.7-3.13), exercised
/// through the REAL <see cref="UpsertItemPolicyHandler"/> + <see cref="ItemPolicyRepository"/> under the REAL
/// Api-host <c>MerchantRequestWriteAuthorizer</c> on SQLite in-memory — mirrors
/// <see cref="InsuranceCheckoutEndToEndTests"/>'s style exactly (real Application handler + real EF
/// repository/unit-of-work + real write floor, not a bare domain unit test — <c>ItemPolicy.Apply</c>'s own
/// invariant branches are already covered exhaustively by <c>Orders.Tests.ItemPolicyTests</c>, task 2). The
/// GRANT proof and the real-SQL-Server persistence-across-restart proof live in
/// <c>Integration.Tests.OrderItemPolicyGrantsTests</c> instead — SQLite has no permission model to prove a
/// GRANT with.
/// </summary>
public sealed class UpsertItemPolicyEndToEndTests : IDisposable
{
    // Every persisted order needs its own number now (IX_Orders_OrderNo is UNIQUE) — a fixed literal in a
    // helper called more than once per database would collide.
    private static int _orderNoCounter;
    private static string NextOrderNo() => $"ORD69{Interlocked.Increment(ref _orderNoCounter):D8}";

    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();
    private static readonly DateTime Dob = new(1985, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public UpsertItemPolicyEndToEndTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = Context(MerchantA);
        setup.Database.EnsureCreated();
    }

    // Mirrors the Api host: MerchantRequestWriteAuthorizer, bound to whichever merchant is asking.
    private MerchantRuntimeDbContext Context(Guid merchant) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(merchant), new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(FakeActor.For(merchant)),
            NoOpSecurityTelemetry.Instance);

    private static UpsertItemPolicyHandler HandlerFor(MerchantRuntimeDbContext db) => new(
        new ItemPolicyRepository(db), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());

    /// <summary>One order, one item, under <paramref name="merchantId"/> — Cancelled when requested (REQ-3.4:
    /// the write path must not gate on Order.Status).</summary>
    private async Task<Guid> CreateItemAsync(Guid merchantId, bool cancelled = false)
    {
        using var db = Context(merchantId);
        var order = OrderAggregate.Create(
            merchantId, Money.Of(2500m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(
                1, Money.Of(2500m, "THB"), "00098-69100/กธ/900001-10", "VMI", "POLICY", null, null, null,
                "Somchai", "Jaidee", "1234567890123", Dob)], orderNo: NextOrderNo());
        if (cancelled)
            order.Cancel();
        db.Add(order);
        await db.SaveChangesAsync();
        return order.Items.Single().Id;
    }

    private static ItemPolicyInput ValidInput(
        PremiumRemittanceStatus status = PremiumRemittanceStatus.NotApplicable, DateOnly? deductedAt = null,
        Money? net = null, Money? gross = null) =>
        new(InsuranceCategory.Voluntary, ReferenceNumberType.PolicyNumber, "POL-0001", null, null, "1กก1234",
            net, gross, status, deductedAt);

    [Fact]
    public async Task Producer_writes_then_reads_back_own_items_policy()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var input = ValidInput(net: Money.Of(1000m, "THB"), gross: Money.Of(1200m, "THB"));

        using (var db = Context(MerchantA))
        {
            var result = await HandlerFor(db).Handle(
                new UpsertItemPolicyCommand(MerchantA, itemId, input, "user-1"), CancellationToken.None);
            Assert.NotEqual(Guid.Empty, result.PolicyId);
        }

        using var verify = Context(MerchantA);
        var policy = await verify.OrderItemPolicies.SingleAsync(p => p.OrderItemId == itemId);
        Assert.Equal("POL-0001", policy.ReferenceNumber);
        Assert.Equal(Money.Of(1000m, "THB"), policy.NetPremium);
        Assert.Equal(Money.Of(1200m, "THB"), policy.GrossPremium);

        var audit = Assert.Single(await verify.OrderItemPolicyAudits.ToListAsync());
        Assert.Equal(AuditOperation.Created, audit.Operation);
        Assert.Equal(ActorKind.Merchant, audit.ActorKind);
        Assert.Contains(nameof(ItemPolicy.ReferenceNumber), audit.ChangeSummary);
    }

    // REQ-3.5 — every write is audited, and ChangeSummary names only the fields that actually changed.
    [Fact]
    public async Task Editing_an_existing_policy_upserts_in_place_and_audits_only_the_changed_field()
    {
        var itemId = await CreateItemAsync(MerchantA);
        using (var db = Context(MerchantA))
            await HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, ValidInput(), "user-1"), CancellationToken.None);

        using (var db = Context(MerchantA))
        {
            var edited = ValidInput() with { EndorsementNumber = "END-1" };
            await HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, edited, "user-1"), CancellationToken.None);
        }

        using var verify = Context(MerchantA);
        Assert.Equal(1, await verify.OrderItemPolicies.CountAsync()); // upsert, not a second row
        var audits = await verify.OrderItemPolicyAudits.OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(AuditOperation.Created, audits[0].Operation);
        Assert.Equal(AuditOperation.Updated, audits[1].Operation);
        Assert.Equal(nameof(ItemPolicy.EndorsementNumber), audits[1].ChangeSummary);
    }

    // REQ-3.3 — cross-merchant is 404, never 403: the query filter makes another merchant's item read as
    // not-existing, so "wrong merchant" and "unknown item" are indistinguishable to the caller.
    [Fact]
    public async Task Producer_targeting_another_merchants_item_gets_NotFound()
    {
        var itemId = await CreateItemAsync(MerchantA);

        using var db = Context(MerchantB);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantB, itemId, ValidInput(), "user-1"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Unknown_item_is_NotFound()
    {
        using var db = Context(MerchantA);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, Guid.NewGuid(), ValidInput(), "user-1"), CancellationToken.None).AsTask());
    }

    // REQ-3.4 — the write path must not gate on Order.Status; a Cancelled order's item is still writable.
    [Fact]
    public async Task A_policy_can_be_written_on_an_item_whose_order_is_cancelled()
    {
        var itemId = await CreateItemAsync(MerchantA, cancelled: true);

        using var db = Context(MerchantA);
        var result = await HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, ValidInput(), "user-1"), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.PolicyId);
    }

    // Representative 400 cases (ItemPolicy.Apply's full invariant matrix is Orders.Tests.ItemPolicyTests'
    // job) — exercised end-to-end THROUGH the real handler/repository/write-floor, not just the domain type.
    [Fact]
    public async Task Type_without_value_is_rejected()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var input = ValidInput() with { ReferenceNumberType = ReferenceNumberType.PolicyNumber, ReferenceNumber = null };

        using var db = Context(MerchantA);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, input, "user-1"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Net_greater_than_gross_is_rejected()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var input = ValidInput(net: Money.Of(1200m, "THB"), gross: Money.Of(1000m, "THB"));

        using var db = Context(MerchantA);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, input, "user-1"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Non_THB_premium_is_rejected()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var input = ValidInput(net: Money.Of(1000m, "USD"), gross: Money.Of(1200m, "USD"));

        using var db = Context(MerchantA);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, input, "user-1"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Deducted_without_DeductedAt_is_rejected()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var input = ValidInput(status: PremiumRemittanceStatus.Deducted, deductedAt: null);

        using var db = Context(MerchantA);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, input, "user-1"), CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Future_DeductedAt_is_rejected()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1));
        var input = ValidInput(status: PremiumRemittanceStatus.Deducted, deductedAt: future);

        using var db = Context(MerchantA);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HandlerFor(db).Handle(new UpsertItemPolicyCommand(MerchantA, itemId, input, "user-1"), CancellationToken.None).AsTask());
    }

    private sealed class FakeActor(bool hasActor, Guid merchantId = default) : IActorContext
    {
        public static FakeActor For(Guid merchantId) => new(true, merchantId);

        public Guid MerchantId => hasActor ? merchantId : throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => hasActor;
    }

    public void Dispose() => _connection.Dispose();
}
