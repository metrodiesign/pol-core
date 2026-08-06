using Contracts;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Orders.Tests;

/// <summary>purchase-flow-completion REQ-7.1-7.5 — what CheckoutConfirmed now carries onto the order: an
/// order number minted from the platform sequence, the channel, the buyer's three contact fields and the
/// per-line discount; and what happens to a v1 payload that has none of them.</summary>
public sealed class OrderEnrichmentTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<CheckoutConfirmedItem> OneLine(decimal discount = 0m, string discountCurrency = "THB") =>
        [new CheckoutConfirmedItem(
            1, Money.Of(15000m, "THB"),
            "00098-69100/กธ/900001-10", "VMI", "POLICY", "POL-1", null, null,
            "Somchai", "Jaidee", "1234567890123", Dob, discount, discountCurrency)];

    private static CheckoutConfirmed Enriched(Guid sessionId, decimal amount = 15000m, decimal discount = 0m) =>
        new(Merchant, sessionId, Money.Of(amount, "THB"), "0812345678", At, OneLine(discount),
            PaymentChannel: "PROMPTPAY_QR", CustomerName: "Somchai Jaidee", CustomerPhone: "0812345678",
            CustomerEmail: "buyer@example.com");

    /// <summary>A payload emitted before this spec: the four new fields are simply absent (REQ-7.5).</summary>
    private static CheckoutConfirmed LegacyV1(Guid sessionId, string? recipient) =>
        new(Merchant, sessionId, Money.Of(15000m, "THB"), recipient, At, OneLine());

    private static (CheckoutConfirmedConsumer Consumer, FakeOrderRepository Orders, FakeOutbox Outbox, FakeOrderNoSequence Numbers)
        Harness(params Order[] seed)
    {
        var orders = new FakeOrderRepository(seed);
        var outbox = new FakeOutbox();
        var numbers = new FakeOrderNoSequence();
        return (new CheckoutConfirmedConsumer(orders, outbox, new FakeUnitOfWork(), new FixedClock(), numbers),
            orders, outbox, numbers);
    }

    // REQ-7.1/7.2 — everything the checkout captured lands on the order, plus a minted number.
    [Fact]
    public async Task The_order_carries_the_number_channel_customer_and_discount()
    {
        var (consumer, orders, _, numbers) = Harness();

        await consumer.Handle(Enriched(Guid.NewGuid(), amount: 13500m, discount: 1500m), default);

        var order = Assert.Single(orders.All);
        Assert.Equal("ORD6900000001", order.OrderNo);
        Assert.Equal(Assert.Single(numbers.Minted), order.OrderNo);
        Assert.Equal("PROMPTPAY_QR", order.PaymentChannel);
        Assert.Equal("Somchai Jaidee", order.CustomerName);
        Assert.Equal("0812345678", order.CustomerPhone);
        Assert.Equal("buyer@example.com", order.CustomerEmail);
        Assert.Equal(Money.Of(1500m, "THB"), Assert.Single(order.Items).Discount);
        Assert.Equal(Money.Of(13500m, "THB"), order.Amount);
    }

    // F-03 — the phone wins over the email as the place the link goes.
    [Fact]
    public async Task The_notification_recipient_is_derived_from_the_phone()
    {
        var (consumer, orders, outbox, _) = Harness();

        await consumer.Handle(Enriched(Guid.NewGuid()), default);

        var order = Assert.Single(orders.All);
        Assert.Equal("0812345678", order.NotificationRecipient);
        var note = Assert.IsType<CustomerOrderNotification>(Assert.Single(outbox.Enqueued));
        Assert.Equal("0812345678", note.Recipient);
        Assert.Equal(order.OrderNo, note.OrderNo);   // REQ-7.1 — the message quotes the number
    }

    [Fact]
    public async Task Without_a_phone_the_email_becomes_the_recipient()
    {
        var (consumer, orders, _, _) = Harness();
        var payload = Enriched(Guid.NewGuid()) with { CustomerPhone = "" };

        await consumer.Handle(payload, default);

        Assert.Equal("buyer@example.com", Assert.Single(orders.All).NotificationRecipient);
    }

    // REQ-7.5 — a v1 payload still opens an order: placeholder customer, zero discount, and the recipient
    // falls all the way back to the old single field, so the customer is still told.
    [Fact]
    public async Task A_v1_payload_still_opens_an_order_and_still_notifies()
    {
        var (consumer, orders, outbox, _) = Harness();

        await consumer.Handle(LegacyV1(Guid.NewGuid(), "legacy@example.com"), default);

        var order = Assert.Single(orders.All);
        Assert.Equal("ORD6900000001", order.OrderNo);
        Assert.Null(order.PaymentChannel);
        Assert.Equal(CustomerContact.UnknownName, order.CustomerName);
        Assert.Equal(string.Empty, order.CustomerPhone);
        Assert.Null(order.CustomerEmail);
        Assert.Equal("legacy@example.com", order.NotificationRecipient);
        Assert.Equal(0m, Assert.Single(order.Items).Discount.Amount);
        Assert.Equal("THB", Assert.Single(order.Items).Discount.Currency);
        var note = Assert.IsType<CustomerOrderNotification>(Assert.Single(outbox.Enqueued));
        Assert.Equal("legacy@example.com", note.Recipient);
    }

    [Fact]
    public async Task A_v1_payload_without_a_recipient_notifies_nobody()
    {
        var (consumer, orders, outbox, _) = Harness();

        await consumer.Handle(LegacyV1(Guid.NewGuid(), null), default);

        Assert.Null(Assert.Single(orders.All).NotificationRecipient);
        Assert.Empty(outbox.Enqueued);
    }

    // A zero discount has no currency of its own — a v1 payload's "THB" default must not fight a USD line.
    [Fact]
    public async Task A_zero_discount_takes_the_lines_currency_not_the_payload_default()
    {
        var (consumer, orders, _, _) = Harness();
        var usdLine = new CheckoutConfirmedItem(
            1, Money.Of(15000m, "USD"),
            "00098-69100/กธ/900001-10", "VMI", "POLICY", null, null, null,
            "Somchai", "Jaidee", "1234567890123", Dob);

        await consumer.Handle(
            new CheckoutConfirmed(Merchant, Guid.NewGuid(), Money.Of(15000m, "USD"), null, At, [usdLine]), default);

        Assert.Equal("USD", Assert.Single(Assert.Single(orders.All).Items).Discount.Currency);
    }

    // REQ-7.1 — a redelivered event must not burn a second number (nor open a second order).
    [Fact]
    public async Task A_replay_mints_no_second_number()
    {
        var sessionId = Guid.NewGuid();
        var existing = Order.Create(
            Merchant, Money.Of(15000m, "THB"), At, OrderLineInputs.OneLine(Money.Of(15000m, "THB")),
            orderNo: "ORD6900000042", checkoutSessionId: sessionId);
        var (consumer, orders, outbox, numbers) = Harness(existing);

        await consumer.Handle(Enriched(sessionId), default);

        Assert.Single(orders.All);
        Assert.Empty(outbox.Enqueued);
        Assert.Empty(numbers.Minted);
        Assert.Equal("ORD6900000042", Assert.Single(orders.All).OrderNo);
    }

    // The number is part of the aggregate's identity — an order cannot exist without one.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_order_cannot_be_created_without_a_number(string orderNo) =>
        Assert.Throws<ArgumentException>(() => Order.Create(
            Merchant, Money.Of(15000m, "THB"), At, OrderLineInputs.OneLine(Money.Of(15000m, "THB")), orderNo));

    // REQ-7.2 — the order amount is the NET sum, so a discounted line that does not add up is refused.
    [Fact]
    public void The_order_amount_must_equal_the_sum_of_the_lines_net_totals()
    {
        var line = OrderLineInputs.OneLine(Money.Of(15000m, "THB"))[0] with { Discount = Money.Of(1500m, "THB") };

        Assert.Throws<ArgumentException>(() => Order.Create(
            Merchant, Money.Of(15000m, "THB"), At, [line], "ORD6900000001"));
        var ok = Order.Create(Merchant, Money.Of(13500m, "THB"), At, [line], "ORD6900000001");
        Assert.Equal(Money.Of(1500m, "THB"), Assert.Single(ok.Items).Discount);
    }
}
