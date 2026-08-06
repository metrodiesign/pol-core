using Checkouts.Domain;
using Checkouts.Domain.Items;
using SharedKernel;

namespace Checkouts.Tests;

/// <summary>purchase-flow-completion REQ-6.1-6.7 — what <see cref="Session.Start"/> now refuses. The
/// endpoint checks the same things earlier (a client sends strings, not enums); these are the domain's own
/// guarantees, so no caller can open a checkout the customer cannot pay or that is priced wrong.</summary>
public sealed class CheckoutEnrichmentTests
{
    private static readonly Guid Merchant = Guid.NewGuid();
    private static readonly Guid Cart = Guid.NewGuid();
    private static readonly DateTime StartAt = new(2026, 6, 23, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Dob = new(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CustomerContact Customer =
        CustomerContact.Of("Somchai Jaidee", "0812345678", "buyer@example.com");

    private static CheckoutItemInput Insured(decimal unitPrice = 15000m, Money? discount = null, string currency = "THB") =>
        new(1, Money.Of(unitPrice, currency),
            "00098-69100/AB/900001-10", "VMI", "POLICY", "POL-0001", null, null,
            "Somchai", "Jaidee", "1234567890123", Dob, discount);

    private static Session Start(decimal amount, params CheckoutItemInput[] lines) =>
        Session.Start(Merchant, Cart, Money.Of(amount, "THB"), StartAt, lines, PaymentChannel.CARD, Customer);

    // REQ-6.3 — a line with no discount carries a zero one in its own currency, never a null.
    [Fact]
    public void A_line_without_a_discount_gets_a_zero_one_in_its_own_currency()
    {
        var item = Assert.Single(Start(15000m, Insured()).Items);

        Assert.Equal(0m, item.Discount.Amount);
        Assert.Equal("THB", item.Discount.Currency);
    }

    // REQ-6.3 — the checkout amount is the NET sum, and the discount is snapshotted per line.
    [Fact]
    public void A_discounted_line_prices_the_checkout_at_the_net_total()
    {
        var session = Start(13500m, Insured(discount: Money.Of(1500m, "THB")));

        Assert.Equal(Money.Of(13500m, "THB"), session.Amount);
        Assert.Equal(Money.Of(1500m, "THB"), Assert.Single(session.Items).Discount);
    }

    [Fact]
    public void Discounts_across_several_lines_all_come_off_the_total()
    {
        var session = Start(23000m, Insured(15000m, Money.Of(1000m, "THB")), Insured(10000m, Money.Of(1000m, "THB")));

        Assert.Equal(Money.Of(23000m, "THB"), session.Amount);
    }

    // REQ-6.4 — a discount bigger than its own line is rejected (a negative one cannot be built at all).
    [Fact]
    public void A_discount_larger_than_its_line_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Start(0m, Insured(discount: Money.Of(15000.01m, "THB"))));

    [Fact]
    public void A_discount_in_another_currency_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Start(15000m, Insured(discount: Money.Of(10m, "USD"))));

    // REQ-6.5 — the stated amount must be exactly the sum of the lines' net totals, both ways.
    [Theory]
    [InlineData(15000)]     // forgot to subtract the discount
    [InlineData(13499)]     // subtracted too much
    public void An_amount_that_is_not_the_net_sum_is_rejected(decimal amount) =>
        Assert.Throws<ArgumentException>(() => Start(amount, Insured(discount: Money.Of(1500m, "THB"))));

    [Fact]
    public void A_line_in_another_currency_than_the_checkout_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Start(15000m, Insured(currency: "USD")));

    // REQ-6.2 — an out-of-range channel value never reaches the column.
    [Fact]
    public void A_channel_outside_the_supported_set_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Session.Start(
            Merchant, Cart, Money.Of(15000m, "THB"), StartAt, [Insured()], (PaymentChannel)99, Customer));

    [Theory]
    [InlineData(PaymentChannel.CARD)]
    [InlineData(PaymentChannel.PROMPTPAY_QR)]
    [InlineData(PaymentChannel.INSTALLMENT)]
    public void Every_supported_channel_is_accepted(PaymentChannel channel)
    {
        var session = Session.Start(
            Merchant, Cart, Money.Of(15000m, "THB"), StartAt, [Insured()], channel, Customer);

        Assert.Equal(channel, session.Channel);
    }

    // The wire values ARE the member names — the payload, the column and the HTTP body all read the same.
    [Fact]
    public void The_channels_wire_values_are_the_member_names()
    {
        Assert.Equal("CARD", PaymentChannel.CARD.ToString());
        Assert.Equal("PROMPTPAY_QR", PaymentChannel.PROMPTPAY_QR.ToString());
        Assert.Equal("INSTALLMENT", PaymentChannel.INSTALLMENT.ToString());
    }
}
