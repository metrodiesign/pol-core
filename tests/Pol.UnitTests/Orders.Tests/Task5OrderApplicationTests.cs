using System.Reflection;
using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

[Trait("Capability", "OrdersLinks")]
public sealed class Task5OrderApplicationTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-10.2")]
    public async Task RequireFirstDelivery_namespaces_key_by_merchant_and_operation()
    {
        var store = new FakeIdempotencyStore();
        var merchantA = Guid.NewGuid();
        var merchantB = Guid.NewGuid();

        // First claim of a raw key for merchant A on order.issue wins.
        await OrderCommandGuards.RequireFirstDeliveryAsync(store, merchantA, "k", "order.issue", default);
        // A different merchant reusing the SAME raw key is its own first delivery, not a replay.
        await OrderCommandGuards.RequireFirstDeliveryAsync(store, merchantB, "k", "order.issue", default);
        // The same merchant reusing the SAME raw key on a DIFFERENT operation is also a first delivery.
        await OrderCommandGuards.RequireFirstDeliveryAsync(store, merchantA, "k", "payment-link.rotate", default);

        // Only the exact merchant + operation + key triple counts as a replay.
        await Assert.ThrowsAsync<ConflictException>(() =>
            OrderCommandGuards.RequireFirstDeliveryAsync(store, merchantA, "k", "order.issue", default));
    }

    [Fact]
    [Trait("Requirement", "REQ-6.1")]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.5")]
    [Trait("Requirement", "REQ-6.6")]
    [Trait("Requirement", "REQ-6.7")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Create_uses_trusted_pricing_and_draft_has_no_payment_link()
    {
        var harness = NewHarness();
        var command = new CreateOrderCommand(
            MerchantId, AccountId, "insurance",
            [new OrderItemRequest("product-from-client", 2,
                """{"schemaVersion":1,"data":{"source":"unit"}}""")],
            new OrderOwnerRequest(null, null), false, "create-1");

        var result = await harness.Create.Handle(command, default);

        Assert.Equal(OrderStatus.Draft, result.Order.OrderStatus);
        Assert.Equal(PaymentStatus.Unpaid, result.Order.PaymentStatus);
        Assert.Null(result.PaymentLink);
        Assert.Null(result.RawToken);
        Assert.Equal(AccountId, result.Order.CreatedByAccountId);
        Assert.Equal(194m, result.Order.TotalAmount.Amount);
        Assert.Single(harness.Orders.Values);
        Assert.Empty(harness.Links.Values);
        Assert.Equal(command.Items, harness.Pricing.LastRequestedItems);
        Assert.DoesNotContain(typeof(OrderItemRequest).GetProperties(), p =>
            p.Name is "Amount" or "Currency" or "UnitPrice" or "LineAmount");
    }

    [Fact]
    [Trait("Requirement", "REQ-6.9")]
    [Trait("Requirement", "REQ-7.1")]
    public async Task Create_issue_now_uses_one_central_issue_path_and_hash_only_link()
    {
        var harness = NewHarness();
        var result = await harness.Create.Handle(new CreateOrderCommand(
            MerchantId, AccountId, "insurance", [new OrderItemRequest("product", 2)],
            new OrderOwnerRequest(null, null), true, "create-issue-1"), default);

        var link = Assert.Single(harness.Links.Values);
        Assert.Equal(OrderStatus.Open, result.Order.OrderStatus);
        Assert.True(result.Order.IsFrozen);
        Assert.Equal(PaymentStatus.Unpaid, result.Order.PaymentStatus);
        Assert.Equal(link.Id, result.PaymentLink!.LinkId);
        Assert.NotNull(result.RawToken);
        Assert.True(link.MatchesHash(harness.Tokens.Hash(result.RawToken!)));
        Assert.DoesNotContain(link.GetType().GetProperties(), p =>
            p.Name.Contains("RawToken", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Requirement", "REQ-6.3")]
    public void Agent_owner_is_derived_and_requested_owner_is_ignored()
    {
        var derivedSale = Guid.NewGuid();
        var derivedBranch = Guid.NewGuid();
        var requestedSale = Guid.NewGuid();
        var requestedBranch = Guid.NewGuid();

        var owner = OrderOwnerPolicy.Resolve(
            OrderActorKind.Agent, MerchantId, AccountId, derivedSale, derivedBranch,
            new OrderOwnerRequest(requestedSale, requestedBranch));

        Assert.Equal(derivedSale, owner.OwnerSaleId);
        Assert.Equal(derivedBranch, owner.OwnerBranchId);
    }

    [Fact]
    [Trait("Requirement", "REQ-6.4")]
    [Trait("Requirement", "REQ-6.5")]
    public void Employee_system_and_merchant_owner_policy_preserves_explicit_or_nullable_scope()
    {
        var sale = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var explicitOwner = new OrderOwnerRequest(sale, branch);

        Assert.Equal(new ResolvedOrderOwner(sale, branch), OrderOwnerPolicy.Resolve(
            OrderActorKind.Employee, MerchantId, AccountId, null, null, explicitOwner));
        Assert.Equal(new ResolvedOrderOwner(sale, branch), OrderOwnerPolicy.Resolve(
            OrderActorKind.System, MerchantId, AccountId, null, null, explicitOwner));
        Assert.Equal(new ResolvedOrderOwner(null, null), OrderOwnerPolicy.Resolve(
            OrderActorKind.Merchant, MerchantId, AccountId, null, null,
            new OrderOwnerRequest(null, null)));
    }

    [Fact]
    [Trait("Requirement", "REQ-6.9")]
    [Trait("Requirement", "REQ-6.10")]
    public async Task Issue_rejects_stale_etag_without_freezing_or_creating_a_link()
    {
        var harness = NewHarness();
        var created = await harness.Create.Handle(new CreateOrderCommand(
            MerchantId, AccountId, "insurance", [new OrderItemRequest("product", 2)],
            new OrderOwnerRequest(null, null), false, "create-draft-1"), default);
        var order = Assert.Single(harness.Orders.Values);
        var versionBefore = order.Version;

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => harness.Issue.Handle(
            new IssueOrderCommand(MerchantId, created.Order.OrderId, versionBefore - 1, "issue-stale-1"), default).AsTask());

        Assert.Equal(versionBefore, order.Version);
        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.False(order.IsFrozen);
        Assert.Empty(harness.Links.Values);
    }

    [Fact]
    [Trait("Requirement", "REQ-6.10")]
    [Trait("Requirement", "REQ-6.11")]
    [Trait("Requirement", "REQ-7.2")]
    public async Task Patch_rejects_an_issued_order_and_cancel_revokes_its_link()
    {
        var harness = NewHarness();
        var created = await harness.Create.Handle(new CreateOrderCommand(
            MerchantId, AccountId, "insurance", [new OrderItemRequest("product", 2)],
            new OrderOwnerRequest(null, null), true, "create-issued-1"), default);
        var order = Assert.Single(harness.Orders.Values);
        var version = order.Version;

        await Assert.ThrowsAsync<ConflictException>(() => harness.Patch.Handle(
            new PatchDraftOrderCommand(
                MerchantId, order.Id, AccountId, "insurance", [new OrderItemRequest("other", 1)],
                new OrderOwnerRequest(null, null), version), default).AsTask());

        var cancel = await harness.Cancel.Handle(new CancelManagedOrderCommand(
            MerchantId, order.Id, version, "customer requested", "cancel-1"), default);

        Assert.Equal(OrderStatus.Cancelled, cancel.Order.OrderStatus);
        Assert.Equal(PaymentLinkStatus.Revoked, Assert.Single(harness.Links.Values).Status);
        Assert.Equal(2, harness.UnitOfWork.SaveCount);
        Assert.Equal(created.Order.OrderId, cancel.Order.OrderId);
    }

    [Fact]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Patch_rejects_ambiguous_metadata_preservation_after_duplicate_product_reorder()
    {
        var harness = NewHarness(duplicateProductPricing: true);
        var created = await harness.Create.Handle(new CreateOrderCommand(
            MerchantId,
            AccountId,
            "insurance",
            [
                new OrderItemRequest("DUPLICATE", 1,
                    """{"schemaVersion":1,"data":{"slot":"a"}}"""),
                new OrderItemRequest("DUPLICATE", 1,
                    """{"schemaVersion":1,"data":{"slot":"b"}}"""),
            ],
            new OrderOwnerRequest(null, null), false, "duplicate-metadata-create"), default);

        var error = await Assert.ThrowsAsync<ConflictException>(() => harness.Patch.Handle(
            new PatchDraftOrderCommand(
                MerchantId,
                created.Order.OrderId,
                AccountId,
                "insurance",
                [new OrderItemRequest("DUPLICATE", 1), new OrderItemRequest("DUPLICATE", 1)],
                new OrderOwnerRequest(null, null),
                created.Order.Version), default).AsTask());

        Assert.Equal("metadata_ambiguous", error.Code);
        Assert.Equal(1, harness.UnitOfWork.SaveCount);
    }

    [Fact]
    [Trait("Requirement", "REQ-6.11")]
    [Trait("Requirement", "REQ-6.12")]
    public async Task Cancel_rejects_paid_and_payment_pending_orders_without_mutation()
    {
        var paid = NewHarness();
        await paid.Create.Handle(new CreateOrderCommand(
            MerchantId, AccountId, "insurance", [new OrderItemRequest("product", 2)],
            new OrderOwnerRequest(null, null), false, "paid-create"), default);
        var paidOrder = Assert.Single(paid.Orders.Values);
        paidOrder.SetPaymentStatus(PaymentStatus.Paid, At.AddMinutes(1));
        var paidVersion = paidOrder.Version;

        var paidError = await Assert.ThrowsAsync<ConflictException>(() => paid.Cancel.Handle(
            new CancelManagedOrderCommand(MerchantId, paidOrder.Id, paidVersion, "cancel", "paid-cancel"), default).AsTask());
        Assert.Equal("order_already_paid", paidError.Code);
        Assert.Equal(paidVersion, paidOrder.Version);

        var pending = NewHarness(blockingPayment: true);
        await pending.Create.Handle(new CreateOrderCommand(
            MerchantId, AccountId, "insurance", [new OrderItemRequest("product", 2)],
            new OrderOwnerRequest(null, null), false, "pending-create"), default);
        var pendingOrder = Assert.Single(pending.Orders.Values);
        var pendingError = await Assert.ThrowsAsync<ConflictException>(() => pending.Cancel.Handle(
            new CancelManagedOrderCommand(MerchantId, pendingOrder.Id, pendingOrder.Version, "cancel", "pending-cancel"), default).AsTask());
        Assert.Equal("payment_pending_verification", pendingError.Code);
        Assert.Equal(OrderStatus.Draft, pendingOrder.Status);
    }

    private static Harness NewHarness(bool blockingPayment = false, bool duplicateProductPricing = false)
    {
        var pricing = new FakePricing(duplicateProductPricing);
        var owners = new FakeOwnerResolver();
        var orders = new FakeWorkflowStore();
        var tokens = new PaymentLinkTokenService(new byte[32]);
        var links = new FakeLinkStore();
        var clock = new FixedClock(At);
        var issuer = new OrderLinkIssuer(tokens, links, clock);
        var replayStore = new FakeReplayStore();
        var replays = new PaymentLinkReplayService(
            replayStore, links, orders, new FakeReplayProtector(), clock);
        var unitOfWork = new FakeUnitOfWork();
        var idempotency = new FakeIdempotencyStore();
        var create = new CreateOrderHandler(
            pricing, owners, orders, new FakeOrderNoSequence(), issuer, replays,
            idempotency, unitOfWork, clock);
        var issue = new IssueOrderHandler(orders, issuer, replays, idempotency, unitOfWork, clock);
        var patch = new PatchDraftOrderHandler(pricing, owners, orders, unitOfWork, clock);
        var cancel = new CancelManagedOrderHandler(
            orders, links, new FakePaymentSessionProbe(blockingPayment), idempotency, unitOfWork, clock);
        return new Harness(create, issue, patch, cancel, orders, links, pricing, tokens, unitOfWork);
    }

    private sealed record Harness(
        CreateOrderHandler Create,
        IssueOrderHandler Issue,
        PatchDraftOrderHandler Patch,
        CancelManagedOrderHandler Cancel,
        FakeWorkflowStore Orders,
        FakeLinkStore Links,
        FakePricing Pricing,
        PaymentLinkTokenService Tokens,
        FakeUnitOfWork UnitOfWork);

    private sealed class FakePricing(bool duplicateProductPricing = false) : ITrustedOrderPricingSource
    {
        public IReadOnlyList<OrderItemRequest> LastRequestedItems { get; private set; } = [];

        public Task<TrustedOrderPricing> PriceAsync(
            Guid merchantId,
            string businessType,
            IReadOnlyList<OrderItemRequest> requestedItems,
            CancellationToken cancellationToken)
        {
            LastRequestedItems = requestedItems;
            var lines = duplicateProductPricing
                ? requestedItems.Select(item => new TrustedOrderLineInput(
                    item.ProductReference, "VMI", "trusted", item.Quantity,
                    Money.Of(100m, "THB"), Money.Of(10m, "THB"), Money.Of(5m, "THB"),
                    Money.Of(95m * item.Quantity, "THB"), "catalog-v1")).ToArray()
                : [new TrustedOrderLineInput(
                    "SERVER-DOC", "VMI", "trusted", 2,
                    Money.Of(100m, "THB"), Money.Of(10m, "THB"), Money.Of(5m, "THB"),
                    Money.Of(195m, "THB"), "catalog-v1")];
            return Task.FromResult(new TrustedOrderPricing(
                "THB", lines, Money.Of(3m, "THB"), Money.Of(2m, "THB")));
        }
    }

    private sealed class FakeOwnerResolver : IOrderOwnerResolver
    {
        public Task<ResolvedOrderOwner> ResolveAsync(
            Guid merchantId, Guid accountId, OrderOwnerRequest requested, CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedOrderOwner(requested.OwnerSaleId, requested.OwnerBranchId));
    }

    private sealed class FakeWorkflowStore : IOrderWorkflowStore
    {
        public List<Order> Values { get; } = [];

        public Task<Order?> GetAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(Values.SingleOrDefault(x => x.MerchantId == merchantId && x.Id == orderId));

        public Task<Order?> GetForUpdateAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
            GetAsync(merchantId, orderId, cancellationToken);

        public void Add(Order order) => Values.Add(order);
    }

    private sealed class FakeLinkStore : IPaymentLinkStore
    {
        public List<PaymentLink> Values { get; } = [];

        public Task<PaymentLink?> GetByHashAsync(Guid merchantId, byte[] tokenHash, CancellationToken cancellationToken) =>
            Task.FromResult(Values.SingleOrDefault(x => x.MerchantId == merchantId && x.MatchesHash(tokenHash)));

        public Task<PaymentLink?> GetLinkAsync(Guid merchantId, Guid linkId, CancellationToken cancellationToken) =>
            Task.FromResult(Values.SingleOrDefault(x => x.MerchantId == merchantId && x.Id == linkId));

        public Task<PaymentLink?> GetActiveForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(Values.SingleOrDefault(x => x.MerchantId == merchantId && x.OrderId == orderId
                && x.Status == PaymentLinkStatus.Active));

        public Task<IReadOnlyList<PaymentLink>> ListForOrderAsync(
            Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentLink>>(
                Values.Where(x => x.MerchantId == merchantId && x.OrderId == orderId).ToList());

        public void Add(PaymentLink link) => Values.Add(link);
    }

    private sealed class FakeReplayStore : IPaymentLinkReplayStore
    {
        private readonly List<PaymentLinkReplay> _values = [];

        public Task<PaymentLinkReplay?> FindReplayAsync(
            Guid merchantId, string operation, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(_values.SingleOrDefault(x => x.MerchantId == merchantId
                && x.Operation == operation && x.IdempotencyKey == idempotencyKey));

        public void Add(PaymentLinkReplay replay) => _values.Add(replay);
    }

    private sealed class FakeReplayProtector : IPaymentLinkReplayProtector
    {
        public string Protect(string rawToken, DateTime expiresAt) => $"protected:{rawToken}";

        public string? Unprotect(string protectedToken) =>
            protectedToken.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedToken[10..]
                : null;
    }

    private sealed class FakeOrderNoSequence : IOrderNoSequence
    {
        private int _next;
        public Task<string> NextAsync(CancellationToken cancellationToken) =>
            Task.FromResult($"ORD69{++_next:D8}");
    }

    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        private readonly HashSet<string> _keys = [];

        public Task<bool> TryBeginAsync(
            IReadOnlyCollection<string> keys,
            string context,
            CancellationToken cancellationToken)
        {
            if (keys.Any(_keys.Contains))
                return Task.FromResult(false);
            foreach (var key in keys)
                _keys.Add(key);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
        {
            SaveCount++;
            return Task.FromResult(1);
        }

        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) => await operation(cancellationToken);
    }

    private sealed class FakePaymentSessionProbe(bool blocking) : IPaymentSessionProbe
    {
        public Task<bool> HasBlockingSessionAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(blocking);
    }

    private sealed class FixedClock(DateTime value) : IClock
    {
        public DateTime UtcNow => value;
    }
}
