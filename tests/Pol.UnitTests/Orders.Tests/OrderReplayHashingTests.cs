using System.Text.Json;
using Checkouts.Application;
using Orders.Application;
using SharedKernel;

namespace Orders.Tests;

public sealed class OrderReplayHashingTests
{
    private static readonly Guid MerchantId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid AccountId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid SaleId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid BranchId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly ResolvedOrderOwner Owner = new(SaleId, BranchId);

    [Fact]
    public void Authorization_proof_version_is_not_part_of_client_replay_intent()
    {
        var versionZero = Command(proofVersion: 0);
        var versionOne = Command(proofVersion: 1);

        Assert.Equal(
            OrderReplayHashing.ForCreate(versionZero, Owner),
            OrderReplayHashing.ForCreate(versionOne, Owner));
    }

    [Fact]
    public void Metadata_property_order_is_canonicalized_for_top_level_and_items()
    {
        var first = Command(
            metadata: "{\"schemaVersion\":1,\"data\":{\"b\":2,\"a\":1}}",
            itemMetadata: "{\"schemaVersion\":1,\"data\":{\"y\":\"two\",\"x\":\"one\"}}");
        var reordered = Command(
            metadata: "{\"data\":{\"a\":1,\"b\":2},\"schemaVersion\":1}",
            itemMetadata: "{\"data\":{\"x\":\"one\",\"y\":\"two\"},\"schemaVersion\":1}");

        Assert.Equal(
            OrderReplayHashing.ForCreate(first, Owner),
            OrderReplayHashing.ForCreate(reordered, Owner));
    }

    [Fact]
    public void Product_owner_money_notification_and_metadata_changes_change_the_hash()
    {
        var baseline = OrderReplayHashing.ForCreate(Command(), Owner);

        Assert.NotEqual(baseline, OrderReplayHashing.ForCreate(Command(product: "other"), Owner));
        Assert.NotEqual(baseline, OrderReplayHashing.ForCreate(
            Command(ownerSale: Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee")), Owner));
        Assert.NotEqual(baseline, OrderReplayHashing.ForCreate(Command(unitPrice: "101.0000"), Owner));
        Assert.NotEqual(baseline, OrderReplayHashing.ForCreate(Command(email: "other@example.test"), Owner));
        Assert.NotEqual(baseline, OrderReplayHashing.ForCreate(
            Command(metadata: "{\"schemaVersion\":1,\"data\":{\"changed\":true}}"), Owner));
    }

    private static CreateOrderCommand Command(
        long proofVersion = 0,
        string product = "DOC-1",
        Guid? ownerSale = null,
        string unitPrice = "100.0000",
        string email = "buyer@example.test",
        string? metadata = "{\"schemaVersion\":1,\"data\":{\"channel\":\"web\"}}",
        string? itemMetadata = "{\"schemaVersion\":1,\"data\":{\"line\":\"one\"}}")
    {
        using var document = JsonDocument.Parse(metadata ?? "null");
        return new CreateOrderCommand(
            MerchantId,
            AccountId,
            " insurance ",
            [new OrderItemRequest(
                $" {product} ",
                1,
                itemMetadata,
                new OrderItemClientSnapshot(product, "Plan", unitPrice, "0.0000", "0.0000", unitPrice))],
            new OrderOwnerRequest(ownerSale ?? SaleId, BranchId),
            true,
            "same-key",
            Customer: CustomerContact.Of("Buyer", "+66800000000", email),
            NotificationRecipient: "+66800000000",
            NotifyOnIssue: true,
            NotificationEmail: email,
            NotificationPhoneNumber: "+66800000000",
            Currency: " thb ",
            OrderDiscountAmount: "0.0000",
            OrderChargeAmount: "0.0000",
            Metadata: document.RootElement.Clone(),
            Authorization: new CommerceAuthorizationProof(
                AccountId, proofVersion, MerchantId, null, "payment.create", null));
    }
}
