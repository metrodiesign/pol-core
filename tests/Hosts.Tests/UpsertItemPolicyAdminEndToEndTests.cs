extern alias ApiHost;

using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
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
/// policy-reference-record task 5 — the admin cross-merchant write escape-hatch (REQ-3.2-admin/3.3-admin),
/// exercised through the REAL <see cref="UpsertItemPolicyAdminHandler"/> + <see cref="AdminItemPolicyWriter"/>
/// under the REAL Api-host <c>AdminItemPolicyWriteAuthorizer</c> on SQLite in-memory — mirrors
/// <see cref="UpsertItemPolicyEndToEndTests"/>'s style exactly (real Application handler + real EF port +
/// real write floor). Items are seeded through the ordinary merchant floor
/// (<c>MerchantRequestWriteAuthorizer</c>) so the admin path is proven to cross a REAL merchant boundary, not
/// a fixture shortcut. <see cref="ItemPolicy.Apply"/>'s own invariant branches are already covered
/// exhaustively by <c>Orders.Tests.ItemPolicyTests</c> (task 2) — not repeated here.
/// </summary>
public sealed class UpsertItemPolicyAdminEndToEndTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();
    private static readonly DateTime Dob = new(1985, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public UpsertItemPolicyAdminEndToEndTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = MerchantContext(MerchantA);
        setup.Database.EnsureCreated();
    }

    // Seeds data through the ordinary merchant floor — mirrors UpsertItemPolicyEndToEndTests' own Context().
    private MerchantRuntimeDbContext MerchantContext(Guid merchant) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(merchant), new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(FakeActor.For(merchant)),
            NoOpSecurityTelemetry.Instance);

    // Mirrors the Api host's admin escape-hatch context (AddAdminItemPolicyWriter): an unbound actor (query
    // filters never matter here — every read is an explicit IgnoreQueryFilters()), AdminItemPolicyWriteAuthorizer
    // bound to the given scope, and a telemetry sink the test can inspect.
    private MerchantRuntimeDbContext AdminContext(IAdminScope scope, ISecurityTelemetry telemetry) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.Unbound, new ApiHost::Api.Persistence.AdminItemPolicyWriteAuthorizer(scope), telemetry);

    private static UpsertItemPolicyAdminHandler HandlerFor(MerchantRuntimeDbContext db, ISecurityTelemetry telemetry) =>
        new(new AdminItemPolicyWriter(db, telemetry), new SystemClock());

    private async Task<Guid> CreateItemAsync(Guid merchantId)
    {
        using var db = MerchantContext(merchantId);
        var order = OrderAggregate.Create(
            merchantId, Money.Of(2500m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(
                Guid.NewGuid(), 1, Money.Of(2500m, "THB"), "00098-69100/กธ/900001-10", "VMI", "POLICY", null, null, null,
                "Somchai", "Jaidee", "1234567890123", Dob)]);
        db.Add(order);
        await db.SaveChangesAsync();
        return order.Items.Single().Id;
    }

    private static ItemPolicyInput ValidInput() =>
        new(InsuranceCategory.Voluntary, ReferenceNumberType.PolicyNumber, "POL-ADM-0001", null, null, "1กก1234",
            null, null, PremiumRemittanceStatus.NotApplicable, null);

    // REQ-3.2-admin — a Super (unrestricted) admin may write ANY merchant's item.
    [Fact]
    public async Task Super_admin_writes_a_policy_on_another_merchants_item()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var telemetry = new FakeTelemetry();
        var scope = new FakeAdminScope(AccessibleMerchants.All);

        using (var db = AdminContext(scope, telemetry))
        {
            var result = await HandlerFor(db, telemetry).Handle(
                new UpsertItemPolicyAdminCommand(itemId, ValidInput(), "admin-1", true, new HashSet<Guid>()),
                CancellationToken.None);
            Assert.NotEqual(Guid.Empty, result.PolicyId);
        }

        using var verify = MerchantContext(MerchantA);
        var policy = await verify.OrderItemPolicies.SingleAsync(p => p.OrderItemId == itemId);
        Assert.Equal(MerchantA, policy.MerchantId); // handler set MerchantId from the item's REAL owner
        Assert.Equal("POL-ADM-0001", policy.ReferenceNumber);

        var audit = Assert.Single(await verify.OrderItemPolicyAudits.ToListAsync());
        Assert.Equal(AuditOperation.Created, audit.Operation);
        Assert.Equal(ActorKind.Admin, audit.ActorKind);

        // The cross-merchant read (LoadAsync's IgnoreQueryFilters) is telemetered every time (design.md
        // "Write guard registration" §Admin plane point 4).
        Assert.Contains(telemetry.Emitted, e => e.Category == DenialCategory.AdminCrossMerchantAction);
    }

    // REQ-3.3-admin — the security boundary this task exists for: a Scoped admin whose accessible set does
    // NOT include the item's real merchant gets 404, indistinguishable from an unknown item (no existence leak).
    [Fact]
    public async Task Scoped_admin_outside_the_accessible_set_gets_NotFound_with_no_existence_leak()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var telemetry = new FakeTelemetry();
        var scope = new FakeAdminScope(AccessibleMerchants.Of(new HashSet<Guid> { MerchantB }));

        using var db = AdminContext(scope, telemetry);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            HandlerFor(db, telemetry).Handle(
                new UpsertItemPolicyAdminCommand(itemId, ValidInput(), "admin-1", false, new HashSet<Guid> { MerchantB }),
                CancellationToken.None).AsTask());

        using var verify = MerchantContext(MerchantA);
        Assert.Equal(0, await verify.OrderItemPolicies.CountAsync()); // denied before any write
    }

    // REQ-3.3-admin — a Scoped admin whose accessible set DOES include the item's real merchant succeeds.
    [Fact]
    public async Task Scoped_admin_inside_the_accessible_set_succeeds()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var telemetry = new FakeTelemetry();
        var scope = new FakeAdminScope(AccessibleMerchants.Of(new HashSet<Guid> { MerchantA }));

        using var db = AdminContext(scope, telemetry);
        var result = await HandlerFor(db, telemetry).Handle(
            new UpsertItemPolicyAdminCommand(itemId, ValidInput(), "admin-1", false, new HashSet<Guid> { MerchantA }),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.PolicyId);
    }

    [Fact]
    public async Task Unknown_item_is_NotFound()
    {
        var telemetry = new FakeTelemetry();
        var scope = new FakeAdminScope(AccessibleMerchants.All);

        using var db = AdminContext(scope, telemetry);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            HandlerFor(db, telemetry).Handle(
                new UpsertItemPolicyAdminCommand(Guid.NewGuid(), ValidInput(), "admin-1", true, new HashSet<Guid>()),
                CancellationToken.None).AsTask());
    }

    // REQ-3.5 — the admin path audits exactly like the merchant path, actor kind Admin.
    [Fact]
    public async Task Editing_an_existing_policy_upserts_in_place_and_audits_as_admin()
    {
        var itemId = await CreateItemAsync(MerchantA);
        var telemetry = new FakeTelemetry();
        var scope = new FakeAdminScope(AccessibleMerchants.All);

        using (var db = AdminContext(scope, telemetry))
            await HandlerFor(db, telemetry).Handle(
                new UpsertItemPolicyAdminCommand(itemId, ValidInput(), "admin-1", true, new HashSet<Guid>()),
                CancellationToken.None);

        using (var db = AdminContext(scope, telemetry))
        {
            var edited = ValidInput() with { EndorsementNumber = "END-ADM-1" };
            await HandlerFor(db, telemetry).Handle(
                new UpsertItemPolicyAdminCommand(itemId, edited, "admin-1", true, new HashSet<Guid>()),
                CancellationToken.None);
        }

        using var verify = MerchantContext(MerchantA);
        Assert.Equal(1, await verify.OrderItemPolicies.CountAsync()); // upsert, not a second row
        var audits = await verify.OrderItemPolicyAudits.OrderBy(a => a.OccurredAt).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.Equal(ActorKind.Admin, a.ActorKind));
        Assert.Equal(AuditOperation.Created, audits[0].Operation);
        Assert.Equal(AuditOperation.Updated, audits[1].Operation);
        Assert.Equal(nameof(ItemPolicy.EndorsementNumber), audits[1].ChangeSummary);
    }

    private sealed class FakeActor(bool hasActor, Guid merchantId = default) : IActorContext
    {
        public static FakeActor For(Guid merchantId) => new(true, merchantId);
        public static readonly FakeActor Unbound = new(false);

        public Guid MerchantId => hasActor ? merchantId : throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => hasActor;
    }

    private sealed class FakeAdminScope(AccessibleMerchants accessible) : IAdminScope
    {
        public bool IsBound => true;
        public Resolution Current => new(Guid.NewGuid(), "admin@org.com", Tier.Scoped, accessible);
        public AccessibleMerchants Accessible => accessible;
    }

    private sealed class FakeTelemetry : ISecurityTelemetry
    {
        public List<DenialEvent> Emitted { get; } = [];
        public void Emit(DenialEvent evt) => Emitted.Add(evt);
    }

    public void Dispose() => _connection.Dispose();
}
