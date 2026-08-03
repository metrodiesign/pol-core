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
        PaymentSessionId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
        OrderId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
        MerchantId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Amount: Money.Of(150_00m, "THB"),
        PspCode: "2c2p",
        ExternalChargeId: "chg_abc123",
        EventId: "evt_xyz789",
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

    // purchase-flow-completion REQ-5.4 — OrderPaid.OrderId was added to a contract already in flight, so a
    // payload enqueued before the field existed still has to deserialize (the outbox will replay it after
    // the deploy). It comes back empty, which is the value the consumer reads as "no double-sell signal".
    [Fact]
    public void An_OrderPaid_payload_without_an_order_id_still_deserializes()
    {
        const string v1Payload = """
            {"merchantId":"44444444-4444-4444-4444-444444444444",
             "productIds":["55555555-5555-5555-5555-555555555555"],
             "occurredAt":"2026-06-21T12:00:00Z"}
            """;

        var restored = JsonSerializer.Deserialize<OrderPaid>(v1Payload, OutboxSerializer.Options);

        Assert.NotNull(restored);
        Assert.Equal(Guid.Empty, restored!.OrderId);
        Assert.Equal(Guid.Parse("55555555-5555-5555-5555-555555555555"), Assert.Single(restored.ProductIds));
    }

    // purchase-flow-completion REQ-7.5 — same situation for the checkout chain: a CheckoutConfirmed enqueued
    // before the channel/customer/discount fields existed still has to replay after the deploy. Every new
    // field comes back at its default, which is exactly what the consumer treats as "a v1 payload".
    [Fact]
    public void A_CheckoutConfirmed_payload_without_the_new_fields_still_deserializes()
    {
        const string v1Payload = """
            {"merchantId":"44444444-4444-4444-4444-444444444444",
             "checkoutSessionId":"66666666-6666-6666-6666-666666666666",
             "amount":{"amount":"15000.0000","currency":"THB"},
             "recipient":"legacy@example.com",
             "occurredAt":"2026-06-21T12:00:00Z",
             "items":[{"productId":"55555555-5555-5555-5555-555555555555","quantity":1,
                       "unitPrice":{"amount":"15000.0000","currency":"THB"},
                       "documentNo":"00098-69100/AB/900001-10","productGroup":"VMI","documentType":"POLICY",
                       "policyNumber":null,"startDate":null,"endDate":null,
                       "insuredFirstName":"Somchai","insuredLastName":"Jaidee",
                       "insuredIdNumber":"1234567890123","insuredDateOfBirth":"1990-01-01T00:00:00Z"}]}
            """;

        var restored = JsonSerializer.Deserialize<CheckoutConfirmed>(v1Payload, OutboxSerializer.Options);

        Assert.NotNull(restored);
        Assert.Null(restored!.PaymentChannel);
        Assert.Null(restored.CustomerName);
        Assert.Null(restored.CustomerPhone);
        Assert.Null(restored.CustomerEmail);
        Assert.Equal("legacy@example.com", restored.Recipient);
        var line = Assert.Single(restored.Items);
        Assert.Equal(0m, line.DiscountAmount);
        Assert.Equal("THB", line.DiscountCurrency);
    }

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
