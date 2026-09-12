using Checkouts.Application;
using Checkouts.Domain;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

[Trait("Capability", "OrdersLinks")]
public sealed class Task5OrdersLinksTests
{
    private static readonly Guid MerchantId = Guid.NewGuid();
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc);

    private static OrderDraftInput Draft(
        Guid? saleId = null,
        Guid? branchId = null,
        Money? discount = null,
        Money? charge = null,
        IReadOnlyList<TrustedOrderLineInput>? items = null) => new(
        MerchantId,
        AccountId,
        "insurance",
        "THB",
        items ??
        [new TrustedOrderLineInput(
            "DOC-1", "VMI", "ประกัน", 2,
            Money.Of(100m, "THB"), Money.Of(10m, "THB"), Money.Of(5m, "THB"),
            Money.Of(195m, "THB"), "catalog-v1")],
        discount ?? Money.Of(3m, "THB"),
        charge ?? Money.Of(2m, "THB"),
        saleId,
        branchId,
        At,
        "ORD6900000001");

    [Fact]
    [Trait("Requirement", "REQ-6.1")]
    [Trait("Requirement", "REQ-6.2")]
    [Trait("Requirement", "REQ-6.5")]
    [Trait("Requirement", "REQ-6.6")]
    public void CreateDraft_records_verified_creator_nullable_owner_and_server_total()
    {
        var order = Order.CreateDraft(Draft());

        Assert.Equal(AccountId, order.CreatedByAccountId);
        Assert.Null(order.OwnerSaleId);
        Assert.Null(order.OwnerBranchIdAtCreation);
        Assert.Equal(OrderStatus.Draft, order.Status);
        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);
        Assert.Equal(195m, order.SubtotalAmount.Amount);
        Assert.Equal(194m, order.TotalAmount.Amount);
        Assert.Equal("THB", order.TotalAmount.Currency);
        Assert.Single(order.Items);
        Assert.Equal(195m, Assert.Single(order.Items).LineAmount.Amount);
    }

    [Fact]
    [Trait("Requirement", "REQ-6.6")]
    [Trait("Requirement", "REQ-6.7")]
    public void CreateDraft_rejects_untrusted_or_inconsistent_money()
    {
        Assert.Throws<ArgumentException>(() => Order.CreateDraft(Draft(items:
            [new TrustedOrderLineInput(
                "DOC-1", "VMI", null, 1,
                Money.Of(100m, "THB"), Money.Zero("THB"), Money.Zero("THB"),
                Money.Of(99m, "THB"), "catalog-v1")])));

        Assert.Throws<ArgumentException>(() => Order.CreateDraft(Draft(items:
            [new TrustedOrderLineInput(
                "DOC-1", "VMI", null, 1,
                Money.Of(100m, "THB"), Money.Of(1m, "USD"), Money.Zero("THB"),
                Money.Of(99m, "THB"), "catalog-v1")])));

        Assert.Throws<ArgumentException>(() => Order.CreateDraft(Draft(
            discount: Money.Of(196m, "THB"))));
    }

    [Fact]
    [Trait("Requirement", "REQ-6.8")]
    [Trait("Requirement", "REQ-6.9")]
    [Trait("Requirement", "REQ-6.10")]
    public void Issue_freezes_order_and_patch_is_rejected_after_issue()
    {
        var order = Order.CreateDraft(Draft());
        order.Issue(At.AddMinutes(1));

        Assert.Equal(OrderStatus.Open, order.Status);
        Assert.True(order.IsFrozen);
        Assert.Equal(PaymentStatus.Unpaid, order.PaymentStatus);
        Assert.Throws<InvalidOperationException>(() => order.PatchDraft(
            Draft(items:
            [new TrustedOrderLineInput(
                "DOC-2", "VMI", null, 1,
                Money.Of(200m, "THB"), Money.Zero("THB"), Money.Zero("THB"),
                Money.Of(200m, "THB"), "catalog-v1")]), At.AddMinutes(2)));
    }

    [Fact]
    [Trait("Requirement", "REQ-7.1")]
    [Trait("Requirement", "REQ-7.2")]
    public void PaymentLink_matches_only_hash_and_rotation_revokes_old_link()
    {
        var order = Order.CreateDraft(Draft());
        order.Issue(At);
        var tokens = new PaymentLinkTokenService(new byte[32]);
        var first = tokens.Mint();
        var old = PaymentLink.Create(order.Id, MerchantId, first.Hash, At, At.AddHours(72));

        Assert.True(old.MatchesHash(tokens.Hash(first.RawToken)));
        Assert.DoesNotContain(old.GetType().GetProperties(),
            p => p.Name.Contains("RawToken", StringComparison.OrdinalIgnoreCase));

        old.Revoke(At.AddMinutes(1));
        var second = tokens.Mint();
        var current = PaymentLink.Create(order.Id, MerchantId, second.Hash, At.AddMinutes(1), At.AddHours(72), old.Id);

        Assert.Equal(PaymentLinkStatus.Revoked, old.Status);
        Assert.Equal(PaymentLinkStatus.Active, current.Status);
        Assert.False(old.IsActiveAt(At.AddMinutes(2)));
        Assert.True(current.IsActiveAt(At.AddMinutes(2)));
    }

    [Fact]
    [Trait("Requirement", "REQ-7.3")]
    public void Replay_expires_and_rejects_changed_intent_without_raw_token()
    {
        var hash = new byte[32];
        var replay = PaymentLinkReplay.Create(
            MerchantId, Guid.NewGuid(), Guid.NewGuid(), "order.issue", "key-1", hash,
            At, At.AddMinutes(30), "protected-token");

        Assert.True(replay.Matches(hash));
        Assert.False(replay.IsExpiredAt(At.AddMinutes(29)));
        Assert.True(replay.IsExpiredAt(At.AddMinutes(30)));
        Assert.False(replay.Matches(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()));
        Assert.DoesNotContain("RawToken", replay.GetType().GetProperties().Select(p => p.Name));
    }
}
