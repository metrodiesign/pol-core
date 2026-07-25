extern alias ApiHost;

using System.Text.Json;
using BuildingBlocks.Application;
using BuildingBlocks.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Orders.Application;
using Orders.Domain.Items;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Orders.Items;
using SharedKernel;
using OrderAggregate = Orders.Domain.Order;
using OrderStatus = Orders.Domain.OrderStatus;

namespace Hosts.Tests;

/// <summary>
/// policy-reference-record task 6 — the policy report read model on both planes (REQ-4), exercised through the
/// REAL <see cref="PolicyReportRepository"/>/<see cref="AdminItemPolicyReader"/> on SQLite in-memory — mirrors
/// <see cref="UpsertItemPolicyEndToEndTests"/>'s style. Items are seeded through the ordinary merchant write
/// floor (<c>MerchantRequestWriteAuthorizer</c> + <see cref="UpsertItemPolicyHandler"/>, already proven end to
/// end by task 4) so this file only proves the REPORT'S own behavior: scope/masking/derived payment
/// status/coalesced remittance/filter/paging. The GRANT proof already covers <c>shop.OrderItemPolicies</c>
/// SELECT (task 4's <c>Integration.Tests.OrderItemPolicyGrantsTests</c>) — this task adds no migration/GRANT,
/// so there is no new Integration.Tests file.
/// </summary>
public sealed class ListPolicyReportEndToEndTests : IDisposable
{
    private static readonly Guid MerchantA = Guid.NewGuid();
    private static readonly Guid MerchantB = Guid.NewGuid();
    private static readonly DateTime Dob = new(1985, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public ListPolicyReportEndToEndTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var setup = MerchantContext(MerchantA);
        setup.Database.EnsureCreated();
    }

    // Mirrors the Api host: MerchantRequestWriteAuthorizer, bound to whichever merchant is asking. Used both
    // to seed items/policies through the real write floor and to run the merchant-plane report query.
    private MerchantRuntimeDbContext MerchantContext(Guid merchant) =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.For(merchant), new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(FakeActor.For(merchant)),
            NoOpSecurityTelemetry.Instance);

    // Admin-plane report reads never call SaveChanges, so the write authorizer is never exercised here — any
    // instance is fine, unbound actor (query filters never matter, every read is IgnoreQueryFilters()).
    private MerchantRuntimeDbContext AdminContext() =>
        new(new DbContextOptionsBuilder<MerchantRuntimeDbContext>().UseSqlite(_connection).Options,
            FakeActor.Unbound, new ApiHost::Api.Persistence.MerchantRequestWriteAuthorizer(FakeActor.Unbound),
            NoOpSecurityTelemetry.Instance);

    private async Task<Guid> CreateItemAsync(Guid merchantId, OrderStatus status = OrderStatus.AwaitingPayment)
    {
        using var db = MerchantContext(merchantId);
        var order = OrderAggregate.Create(
            merchantId, Money.Of(2500m, "THB"), DateTime.UtcNow,
            [new OrderItemInput(
                Guid.NewGuid(), 1, Money.Of(2500m, "THB"), Money.Of(1_000_000m, "THB"), 30, "Muang Thai Insurance",
                "Somchai", "Jaidee", "1234567890123", Dob)]);
        switch (status)
        {
            case OrderStatus.Paid: order.MarkPaid(Money.Of(2500m, "THB"), DateTime.UtcNow); break;
            case OrderStatus.Cancelled: order.Cancel(); break;
        }
        db.Add(order);
        await db.SaveChangesAsync();
        return order.Items.Single().Id;
    }

    // Seeds a policy through the REAL merchant write path (task 4, already proven) — this file is not
    // re-testing Apply's invariants, just seeding report fixtures.
    private async Task SetPolicyAsync(Guid merchantId, Guid itemId, ItemPolicyInput input)
    {
        using var db = MerchantContext(merchantId);
        var handler = new UpsertItemPolicyHandler(
            new ItemPolicyRepository(db), new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance), new SystemClock());
        await handler.Handle(new UpsertItemPolicyCommand(merchantId, itemId, input, "user-1"), CancellationToken.None);
    }

    private static ItemPolicyInput PolicyInput(
        string referenceNumber = "POL-0001", PremiumRemittanceStatus status = PremiumRemittanceStatus.NotApplicable,
        DateOnly? deductedAt = null) =>
        new(InsuranceCategory.Voluntary, ReferenceNumberType.PolicyNumber, referenceNumber, "END-1", "REM-1",
            "1กก1234", Money.Of(1000m, "THB"), Money.Of(1200m, "THB"), status, deductedAt);

    private static Task<PagedResult<PolicyReportItem>> MerchantReport(
        MerchantRuntimeDbContext db, Guid merchantId, IReadOnlyList<FilterOption>? filters = null,
        int page = 1, int limit = 25) =>
        new PolicyReportRepository(db, NullLogger<PolicyReportRepository>.Instance).ListAsync(
            new ListPolicyReportQuery { MerchantId = merchantId, Page = page, Limit = limit, Filters = filters ?? [] },
            CancellationToken.None);

    private static Task<PagedResult<PolicyReportItem>> AdminReport(
        MerchantRuntimeDbContext db, bool unrestricted, IReadOnlySet<Guid>? accessible = null, Guid? merchantId = null) =>
        new AdminItemPolicyReader(db, NoOpSecurityTelemetry.Instance, NullLogger<AdminItemPolicyReader>.Instance).ListAsync(
            new ListPolicyReportAdminQuery
            {
                IsUnrestrictedAdmin = unrestricted, AccessibleMerchantIds = accessible ?? new HashSet<Guid>(), MerchantId = merchantId,
            },
            CancellationToken.None);

    // REQ-4.2/4.5/4.6 — merchant sees only its own item, InsuredIdNumber is masked, reference numbers are not.
    [Fact]
    public async Task Merchant_sees_only_its_own_items_and_masks_only_the_id_number()
    {
        var itemA = await CreateItemAsync(MerchantA);
        await SetPolicyAsync(MerchantA, itemA, PolicyInput());
        await CreateItemAsync(MerchantB);

        using var db = MerchantContext(MerchantA);
        var report = await MerchantReport(db, MerchantA);

        var row = Assert.Single(report.Items);
        Assert.Equal("Somchai Jaidee", row.InsuredName);
        Assert.Equal("****0123", row.InsuredIdNumberMasked);   // last 4 of "1234567890123"
        Assert.Equal("POL-0001", row.ReferenceNumber);          // not masked (REQ-4.6)
        Assert.Equal("END-1", row.EndorsementNumber);
        Assert.Equal("REM-1", row.RenewalReminderNumber);
        Assert.Equal("1กก1234", row.InsuredObjectReference);
        Assert.Null(row.MerchantId);                             // admin-only field (REQ-4.2)
    }

    // REQ-4.2/4.4 — admin sees cross-merchant, honors the accessible set AND the optional ?merchantId= filter,
    // and a Scoped admin naming a merchant outside its accessible set gets an empty page, never a leak.
    [Fact]
    public async Task Admin_sees_cross_merchant_and_honors_accessible_set_and_merchantId_filter()
    {
        await CreateItemAsync(MerchantA);
        await CreateItemAsync(MerchantB);

        using var db = AdminContext();

        var superAll = await AdminReport(db, unrestricted: true);
        Assert.Equal(2, superAll.Total);
        Assert.All(superAll.Items, i => Assert.NotNull(i.MerchantId));   // populated on the admin plane

        var superFilteredToB = await AdminReport(db, unrestricted: true, merchantId: MerchantB);
        Assert.Equal(MerchantB, Assert.Single(superFilteredToB.Items).MerchantId);

        var scopedToA = await AdminReport(db, unrestricted: false, accessible: new HashSet<Guid> { MerchantA });
        Assert.Equal(MerchantA, Assert.Single(scopedToA.Items).MerchantId);

        // Scoped to A, but asking for B — the two filters intersect to nothing, not a leak.
        var scopedToAAskingForB = await AdminReport(
            db, unrestricted: false, accessible: new HashSet<Guid> { MerchantA }, merchantId: MerchantB);
        Assert.Empty(scopedToAAskingForB.Items);
        Assert.Equal(0, scopedToAAskingForB.Total);
    }

    // REQ-4.3/4.7 — paymentStatus is derived from Order.Status (never a stored field) and is never blank, even
    // for an item that has no ItemPolicy row at all.
    [Theory]
    [InlineData(OrderStatus.AwaitingPayment, "รอชำระเงิน")]
    [InlineData(OrderStatus.Paid, "ชำระสำเร็จ")]
    [InlineData(OrderStatus.Cancelled, "ยกเลิก")]
    public async Task PaymentStatus_is_derived_from_OrderStatus_and_never_blank(OrderStatus status, string expectedLabel)
    {
        await CreateItemAsync(MerchantA, status);   // no policy written at all — REQ-1.7

        using var db = MerchantContext(MerchantA);
        var row = Assert.Single((await MerchantReport(db, MerchantA)).Items);

        Assert.Equal(expectedLabel, row.PaymentStatus);
        Assert.NotNull(row.PaymentStatus);
        Assert.NotEmpty(row.PaymentStatus);

        // REQ-4.7 — external-reference columns blank/N-A for an item with no external data, not hidden.
        Assert.Null(row.ReferenceNumber);
        Assert.Null(row.EndorsementNumber);
        Assert.Equal(PremiumRemittanceStatus.NotApplicable, row.PremiumRemittanceStatus);
        Assert.Null(row.NetPremium);
        Assert.Null(row.GrossPremium);
    }

    // REQ-4.4/4.7 — filtering premiumRemittanceStatus=NotApplicable must also match items with NO ItemPolicy
    // row at all (the LEFT JOIN coalesce), not just ones explicitly set to NotApplicable.
    [Fact]
    public async Task Filtering_by_NotApplicable_remittance_matches_items_with_no_policy_row()
    {
        var deducted = await CreateItemAsync(MerchantA);
        await SetPolicyAsync(MerchantA, deducted, PolicyInput(
            referenceNumber: "POL-DEDUCTED", status: PremiumRemittanceStatus.Deducted, deductedAt: new DateOnly(2026, 1, 1)));
        var noPolicy = await CreateItemAsync(MerchantA);   // never written — no ItemPolicy row

        using var db = MerchantContext(MerchantA);
        var filters = new List<FilterOption>
        {
            new("premiumRemittanceStatus", FilterOperator.Equals, JsonSerializer.SerializeToElement("NotApplicable"), null),
        };
        var report = await MerchantReport(db, MerchantA, filters);

        var row = Assert.Single(report.Items);
        Assert.Equal(noPolicy, row.ItemId);
        Assert.Equal(PremiumRemittanceStatus.NotApplicable, row.PremiumRemittanceStatus);
    }

    // REQ-4.4 — filter by paymentStatus + paging both work.
    [Fact]
    public async Task Filtering_by_paymentStatus_and_paging_work()
    {
        await CreateItemAsync(MerchantA, OrderStatus.AwaitingPayment);
        await CreateItemAsync(MerchantA, OrderStatus.Paid);
        await CreateItemAsync(MerchantA, OrderStatus.Paid);

        using var db = MerchantContext(MerchantA);

        var paidOnly = await MerchantReport(db, MerchantA,
            [new FilterOption("paymentStatus", FilterOperator.Equals, JsonSerializer.SerializeToElement("Paid"), null)]);
        Assert.Equal(2, paidOnly.Total);
        Assert.All(paidOnly.Items, i => Assert.Equal("ชำระสำเร็จ", i.PaymentStatus));

        var page1 = await MerchantReport(db, MerchantA, page: 1, limit: 2);
        var page2 = await MerchantReport(db, MerchantA, page: 2, limit: 2);
        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        Assert.DoesNotContain(page1.Items.Select(i => i.ItemId), id => page2.Items.Select(i => i.ItemId).Contains(id));
    }

    // design.md m5 — NetPremium/GrossPremium MUST stay Money? on the wire type so MoneyJsonConverter writes
    // JSON null for an unset premium instead of a default-struct's garbage {"amount":"0.0000","currency":null}.
    [Fact]
    public void Unset_premiums_serialize_as_JSON_null_not_a_default_Money_struct()
    {
        var item = new PolicyReportItem(
            Guid.NewGuid(), Guid.NewGuid(), "Somchai Jaidee", "****0123", null, null, null, null, null, null,
            null, null, PremiumRemittanceStatus.NotApplicable, null, "รอชำระเงิน", null);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new MoneyJsonConverter());
        var json = JsonSerializer.Serialize(item, options);

        Assert.Contains("\"netPremium\":null", json);
        Assert.Contains("\"grossPremium\":null", json);
        Assert.DoesNotContain("0.0000", json);
    }

    private sealed class FakeActor(bool hasActor, Guid merchantId = default) : IActorContext
    {
        public static FakeActor For(Guid merchantId) => new(true, merchantId);
        public static readonly FakeActor Unbound = new(false);

        public Guid MerchantId => hasActor ? merchantId : throw new InvalidOperationException("No actor bound.");
        public Guid? UserId => null;
        public bool HasActor => hasActor;
    }

    public void Dispose() => _connection.Dispose();
}
