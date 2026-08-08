using System.Text.Json;
using BuildingBlocks.Infrastructure.Outbox;
using Contracts;
using SharedKernel;

namespace BuildingBlocks.Tests;

/// <summary>
/// Observable contract of <see cref="OutboxSerializer.Options"/>: a <see cref="PaymentPaid"/> event
/// — including its <see cref="Money"/> value — survives a serialize/deserialize round-trip via the
/// shared outbox options (camelCase + MoneyJsonConverter). This is exactly what the outbox writes
/// and the dispatcher reads back, so the value the consumer sees must equal the one produced.
/// </summary>
public sealed class OutboxSerializerTests
{
    private static PaymentPaid SampleEvent() => new(
        EventId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        PaymentSessionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        OrderId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        MerchantId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Amount: Money.Of(150_00m, "THB"),
        Method: "card",
        PspCode: "2c2p",
        ExternalChargeId: "chg_abc123",
        PspEventId: "evt_xyz789",
        OccurredAt: new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void PaymentPaid_round_trips_through_outbox_options()
    {
        var original = SampleEvent();

        var json = JsonSerializer.Serialize(original, OutboxSerializer.Options);
        var restored = JsonSerializer.Deserialize<PaymentPaid>(json, OutboxSerializer.Options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void PaymentPaid_round_trip_preserves_money_amount_and_currency()
    {
        var original = SampleEvent();

        var json = JsonSerializer.Serialize(original, OutboxSerializer.Options);
        var restored = JsonSerializer.Deserialize<PaymentPaid>(json, OutboxSerializer.Options);

        Assert.NotNull(restored);
        Assert.Equal(150_00m, restored!.Amount.Amount);
        Assert.Equal("THB", restored.Amount.Currency);
    }

    // The Contracts.OrderPaid integration event was retired with the catalogue mirror
    // (products-external-source-of-truth REQ-8.3), so its serialization test went away with it — double-sell is
    // now inferred from Orders by IDoubleSellAuditor, not carried on an outbox event.

    // REQ-7.5 — and the notification the consumer enqueues alongside it.
    [Fact]
    public void A_CustomerOrderNotification_payload_without_an_order_number_still_deserializes()
    {
        const string v1Payload = """
            {"merchantId":"44444444-4444-4444-4444-444444444444",
             "orderId":"77777777-7777-7777-7777-777777777777",
             "recipient":"legacy@example.com","summaryToken":"tok",
             "occurredAt":"2026-06-21T12:00:00Z"}
            """;

        var restored = JsonSerializer.Deserialize<CustomerOrderNotification>(v1Payload, OutboxSerializer.Options);

        Assert.NotNull(restored);
        Assert.Null(restored!.OrderNo);
        Assert.Equal("legacy@example.com", restored.Recipient);
    }

    [Fact]
    public void PaymentPaid_serializes_property_names_in_camelCase()
    {
        var json = JsonSerializer.Serialize(SampleEvent(), OutboxSerializer.Options);

        // Web defaults => camelCase property names in the persisted payload.
        Assert.Contains("\"paymentSessionId\"", json);
        Assert.Contains("\"occurredAt\"", json);
        Assert.DoesNotContain("\"PaymentSessionId\"", json);
    }
}
