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
        TenantId: Guid.Parse("44444444-4444-4444-4444-444444444444"),
        Amount: Money.Of(150_00, "THB"),
        PspCode: "2c2p",
        ExternalChargeId: "chg_abc123",
        EventId: "evt_xyz789",
        OccurredAtUtc: new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc));

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
        Assert.Equal(150_00, restored!.Amount.MinorUnits);
        Assert.Equal("THB", restored.Amount.Currency);
    }

    [Fact]
    public void PaymentPaid_serializes_property_names_in_camelCase()
    {
        var json = JsonSerializer.Serialize(SampleEvent(), OutboxSerializer.Options);

        // Web defaults => camelCase property names in the persisted payload.
        Assert.Contains("\"paymentSessionId\"", json);
        Assert.Contains("\"occurredAtUtc\"", json);
        Assert.DoesNotContain("\"PaymentSessionId\"", json);
    }
}
