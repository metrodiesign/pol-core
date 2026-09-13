using BuildingBlocks.Application;
using Checkouts.Application;
using Checkouts.Domain;
using Microsoft.AspNetCore.DataProtection;
using Orders.Application;
using Orders.Domain;
using Persistence.MerchantRuntime.Orders;
using SharedKernel;

namespace Orders.Tests;

[Trait("Capability", "OrdersLinks")]
public sealed class Task5PaymentLinkAccessTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly DateTime At = DateTime.UtcNow.AddMinutes(30);

    [Fact]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.2")]
    [Trait("Requirement", "REQ-7.3")]
    public async Task Rotate_revokes_old_link_without_creating_another_order_and_replay_returns_same_token()
    {
        var h = NewLinkHarness();
        var order = NewIssuedOrder(h);
        var first = Assert.Single(h.Links.Values);
        var command = new RotatePaymentLinkCommand(MerchantId, order.Id, order.Version, "rotate-1");

        var rotated = await h.Rotate.Handle(command, default);
        var replay = await h.Rotate.Handle(command, default);

        Assert.Single(h.Orders.Values);
        Assert.Equal(2, h.Links.Values.Count);
        Assert.Equal(PaymentLinkStatus.Revoked, first.Status);
        Assert.Equal(PaymentLinkStatus.Active, Assert.Single(h.Links.Values, x => x.Status == PaymentLinkStatus.Active).Status);
        Assert.Equal(rotated.RawToken, replay.RawToken);
        Assert.True(replay.Replayed);
        Assert.Equal(rotated.Order.Version, replay.Order.Version);
    }

    [Fact]
    [Trait("Requirement", "REQ-7.3")]
    public async Task Rotate_notification_without_persisted_recipient_returns_conflict_before_link_write()
    {
        var h = NewLinkHarness();
        var order = NewIssuedOrder(h);

        var error = await Assert.ThrowsAsync<ConflictException>(() => h.Rotate.Handle(
            new RotatePaymentLinkCommand(
                MerchantId, order.Id, order.Version, "rotate-missing-recipient",
                SendNotification: true), default).AsTask());

        Assert.Equal("notification_recipient_required", error.Code);
        Assert.Single(h.Links.Values);
        Assert.Equal(OrderStatus.Open, order.Status);
    }

    [Fact]
    [Trait("Requirement", "REQ-7.3")]
    public async Task Rotate_rejects_changed_intent_and_expired_protected_replay()
    {
        var h = NewLinkHarness();
        var order = NewIssuedOrder(h);
        var original = new RotatePaymentLinkCommand(MerchantId, order.Id, order.Version, "rotate-2");
        await h.Rotate.Handle(original, default);

        var changed = await Assert.ThrowsAsync<ConflictException>(() => h.Rotate.Handle(
            original with { ExpectedVersion = original.ExpectedVersion + 1 }, default).AsTask());
        Assert.Equal("idempotency_conflict", changed.Code);

        h.Clock.Value = At.AddHours(1);
        var expired = await Assert.ThrowsAsync<ConflictException>(() => h.Rotate.Handle(original, default).AsTask());
        Assert.Equal("idempotent_secret_expired", expired.Code);
    }

    [Fact]
    [Trait("Requirement", "REQ-7.2")]
    public async Task Revoke_closes_link_but_leaves_order_lifecycle_unchanged()
    {
        var h = NewLinkHarness();
        var order = NewIssuedOrder(h);
        var link = Assert.Single(h.Links.Values);

        var result = await h.Revoke.Handle(
            new RevokePaymentLinkCommand(MerchantId, link.Id, "revoke-1"), default);

        Assert.Equal(PaymentLinkStatus.Revoked, link.Status);
        Assert.Equal(PaymentLinkStatus.Revoked, result.Status);
        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);
    }

    [Fact]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.4")]
    public async Task Exchange_hashes_body_token_and_issues_order_only_capability()
    {
        var tokens = new PaymentLinkTokenService(new byte[32]);
        var linkId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var reader = new FakeCheckoutReader
        {
            Link = new CheckoutLinkSnapshot(
                linkId, MerchantId, orderId, PaymentLinkStatus.Active, At, At.AddHours(72), null, 1,
                OrderStatus.Open, PaymentStatus.Unpaid, 4),
        };
        var capability = new FakeCapabilityService();
        var handler = new ExchangeCheckoutAccessHandler(
            tokens, reader, capability, new FixedClock(At));

        var result = await handler.Handle(new ExchangeCheckoutAccessCommand("opaque-token"), default);

        Assert.Equal(orderId, result.OrderId);
        Assert.Equal(4, result.OrderVersion);
        Assert.Equal("proof", result.Proof);
        Assert.Equal("csrf", result.CsrfToken);
        Assert.DoesNotContain("eyJ", result.Proof, StringComparison.Ordinal);
        Assert.Equal(tokens.Hash("opaque-token"), reader.LastHash);
    }

    [Fact]
    [Trait("Requirement", "REQ-7.4")]
    public async Task Exchange_rejects_unknown_revoked_and_expired_links()
    {
        var tokens = new PaymentLinkTokenService(new byte[32]);
        var capability = new FakeCapabilityService();
        var clock = new FixedClock(At);
        var reader = new FakeCheckoutReader();
        var handler = new ExchangeCheckoutAccessHandler(tokens, reader, capability, clock);

        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(
            new ExchangeCheckoutAccessCommand("missing"), default).AsTask());

        reader.Link = new CheckoutLinkSnapshot(
            Guid.NewGuid(), MerchantId, Guid.NewGuid(), PaymentLinkStatus.Revoked, At, At.AddHours(72), At, 2,
            OrderStatus.Open, PaymentStatus.Unpaid, 1);
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(
            new ExchangeCheckoutAccessCommand("revoked"), default).AsTask());

        reader.Link = reader.Link with { LinkStatus = PaymentLinkStatus.Active, LinkExpiresAt = At.AddMinutes(-1) };
        await Assert.ThrowsAsync<GoneException>(() => handler.Handle(
            new ExchangeCheckoutAccessCommand("expired"), default).AsTask());
    }

    [Fact]
    [Trait("Requirement", "REQ-7.5")]
    public async Task Summary_returns_allowlisted_fields_and_rejects_tampered_or_stale_capability()
    {
        var clock = new FixedClock(At);
        var reader = new FakeCheckoutReader
        {
            Summary = new CheckoutOrderSnapshot(
                Guid.NewGuid(), Guid.NewGuid(), 7, PaymentLinkStatus.Active, At.AddHours(1), "ORD6900000001",
                "Merchant name", OrderStatus.Open, PaymentStatus.Unpaid, Money.Of(194m, "THB"),
                [new CheckoutSummaryLine("DOC-1", "VMI", "Name", 1, Money.Of(194m, "THB"))]),
        };
        var capability = new FakeCapabilityService { Current = new CheckoutCapability(
            reader.Summary.OrderId, reader.Summary.LinkId, 7, "proof", "csrf", At.AddMinutes(30)) };
        var handler = new GetCheckoutSummaryHandler(reader, capability, clock);

        var result = await handler.Handle(new GetCheckoutSummaryQuery("proof"), default);

        Assert.Equal("ORD6900000001", result.OrderNo);
        Assert.Equal("Merchant name", result.MerchantName);
        Assert.Equal(194m, result.TotalAmount.Amount);
        Assert.Single(result.Lines);
        Assert.DoesNotContain("RawOrderSnapshot", result.GetType().GetProperties().Select(x => x.Name));
        Assert.DoesNotContain("OwnerSaleId", result.GetType().GetProperties().Select(x => x.Name));
        Assert.DoesNotContain("PaymentSessionId", result.GetType().GetProperties().Select(x => x.Name));

        capability.Current = capability.Current with { OrderVersion = 6 };
        await Assert.ThrowsAsync<ConflictException>(() => handler.Handle(
            new GetCheckoutSummaryQuery("proof"), default).AsTask());
        capability.Invalid = true;
        await Assert.ThrowsAsync<AccessDeniedException>(() => handler.Handle(
            new GetCheckoutSummaryQuery("tampered"), default).AsTask());
    }

    [Fact]
    [Trait("Requirement", "REQ-7.3")]
    [Trait("Requirement", "REQ-7.4")]
    public void Data_protection_replay_and_capability_reject_tampering()
    {
        var now = DateTime.UtcNow;
        var provider = new EphemeralDataProtectionProvider();
        var replay = new DataProtectedPaymentLinkReplayProtector(provider);
        var protectedToken = replay.Protect("raw-token", now.AddMinutes(30));
        Assert.NotEqual("raw-token", protectedToken);
        Assert.Equal("raw-token", replay.Unprotect(protectedToken));
        Assert.Null(replay.Unprotect(protectedToken + "tampered"));

        var capability = new DataProtectedCheckoutCapabilityService(provider);
        var issued = capability.Issue(Guid.NewGuid(), Guid.NewGuid(), 4, now.AddHours(1), now);
        Assert.True(capability.TryRead(issued.Proof, out var read));
        Assert.Equal(issued.OrderId, read.OrderId);
        Assert.Equal(issued.LinkId, read.LinkId);
        Assert.False(capability.TryRead(issued.Proof + "tampered", out _));
    }

    private static LinkHarness NewLinkHarness()
    {
        var orders = new FakeWorkflowStore();
        var links = new FakeLinkStore();
        var replays = new FakeReplayStore();
        var clock = new MutableClock(At);
        var tokens = new PaymentLinkTokenService(new byte[32]);
        var issuer = new OrderLinkIssuer(tokens, links, clock);
        var replayService = new PaymentLinkReplayService(
            replays, links, orders, new FakeReplayProtector(), clock);
        var unit = new FakeUnitOfWork();
        var idempotency = new FakeIdempotencyStore();
        var rotate = new RotatePaymentLinkHandler(
            orders, links, issuer, replayService, idempotency, unit, clock);
        var revoke = new RevokePaymentLinkHandler(links, orders, idempotency, unit, clock);
        return new LinkHarness(orders, links, replays, clock, rotate, revoke);
    }

    private static Order NewIssuedOrder(LinkHarness h)
    {
        var order = Order.CreateDraft(new OrderDraftInput(
            MerchantId, AccountId, "insurance", "THB",
            [new TrustedOrderLineInput("DOC-1", "VMI", null, 1, Money.Of(100m, "THB"),
                Money.Zero("THB"), Money.Zero("THB"), Money.Of(100m, "THB"), "catalog")],
            Money.Zero("THB"), Money.Zero("THB"), null, null, At, "ORD6900000001"));
        order.Issue(At);
        h.Orders.Add(order);
        var issued = new OrderLinkIssuer(new PaymentLinkTokenService(new byte[32]), h.Links, h.Clock).Issue(order);
        return order;
    }

    private sealed record LinkHarness(
        FakeWorkflowStore Orders,
        FakeLinkStore Links,
        FakeReplayStore Replays,
        MutableClock Clock,
        RotatePaymentLinkHandler Rotate,
        RevokePaymentLinkHandler Revoke);

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
        public Task<IReadOnlyList<PaymentLink>> ListForOrderAsync(Guid merchantId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PaymentLink>>(Values.Where(x => x.MerchantId == merchantId && x.OrderId == orderId).ToList());
        public void Add(PaymentLink link) => Values.Add(link);
    }

    private sealed class FakeReplayStore : IPaymentLinkReplayStore
    {
        public List<PaymentLinkReplay> Values { get; } = [];
        public Task<PaymentLinkReplay?> FindReplayAsync(Guid merchantId, string operation, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(Values.SingleOrDefault(x => x.MerchantId == merchantId && x.Operation == operation
                && x.IdempotencyKey == idempotencyKey));
        public void Add(PaymentLinkReplay replay) => Values.Add(replay);
    }

    private sealed class FakeReplayProtector : IPaymentLinkReplayProtector
    {
        public string Protect(string rawToken, DateTime expiresAt) => $"p:{rawToken}";
        public string? Unprotect(string protectedToken) => protectedToken.StartsWith("p:", StringComparison.Ordinal)
            ? protectedToken[2..] : null;
    }

    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        private readonly HashSet<string> _keys = [];
        public Task<bool> TryBeginAsync(IReadOnlyCollection<string> keys, string context, CancellationToken cancellationToken)
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
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(1);
        public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
            await operation(cancellationToken);
    }

    private sealed class MutableClock(DateTime value) : IClock
    {
        public DateTime Value { get; set; } = value;
        public DateTime UtcNow => Value;
    }

    private sealed class FixedClock(DateTime value) : IClock
    {
        public DateTime UtcNow => value;
    }

    private sealed class FakeCheckoutReader : ICustomerCheckoutReader
    {
        public CheckoutLinkSnapshot? Link { get; set; }
        public CheckoutOrderSnapshot? Summary { get; set; }
        public byte[]? LastHash { get; private set; }
        public Task<CheckoutLinkSnapshot?> FindByHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
        {
            LastHash = tokenHash;
            return Task.FromResult(Link);
        }
        public Task<CheckoutOrderSnapshot?> GetSummaryAsync(Guid orderId, Guid linkId, CancellationToken cancellationToken) =>
            Task.FromResult(Summary);
    }

    private sealed class FakeCapabilityService : ICheckoutCapabilityService
    {
        public CheckoutCapability? Current { get; set; }
        public bool Invalid { get; set; }
        public CheckoutCapability Issue(Guid orderId, Guid linkId, long orderVersion, DateTime linkExpiresAt, DateTime now) =>
            Current ??= new CheckoutCapability(orderId, linkId, orderVersion, "proof", "csrf", linkExpiresAt);
        public bool TryRead(string proof, out CheckoutCapability capability)
        {
            capability = Current!;
            return !Invalid && proof == "proof" && Current is not null;
        }
    }
}
