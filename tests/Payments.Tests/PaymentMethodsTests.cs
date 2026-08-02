using Payments.Domain;

namespace Payments.Tests;

/// <summary>
/// Pure domain tests for the canonical method vocabulary. They pin the code literals (they are DB/seed
/// and wire values, so a rename must break a test) and both directions of the normalizer: what it accepts
/// case/whitespace-insensitively, and that anything outside the vocabulary is an ArgumentException (400)
/// rather than reaching an adapter and surfacing as a 500.
/// </summary>
public sealed class PaymentMethodsTests
{
    [Fact]
    public void Codes_are_the_stable_lowercase_wire_values()
    {
        Assert.Equal("card", PaymentMethods.Card);
        Assert.Equal("promptpay", PaymentMethods.PromptPay);
        Assert.Equal("installment", PaymentMethods.Installment);
    }

    [Theory]
    [InlineData("card", "card")]
    [InlineData("CARD", "card")]
    [InlineData(" card ", "card")]
    [InlineData("PromptPay", "promptpay")]
    [InlineData("  INSTALLMENT\t", "installment")]
    public void Normalize_trims_and_lowercases_a_known_code(string input, string expected) =>
        Assert.Equal(expected, PaymentMethods.Normalize(input));

    [Theory]
    [InlineData("paypal")]
    [InlineData("CC")]        // the 2C2P wire channel, not our vocabulary
    [InlineData("credit card")]
    public void Normalize_rejects_a_method_outside_the_vocabulary(string input)
    {
        var ex = Assert.Throws<ArgumentException>(() => PaymentMethods.Normalize(input));

        Assert.Equal("method", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_rejects_a_blank_method(string input) =>
        Assert.Throws<ArgumentException>(() => PaymentMethods.Normalize(input));

    [Fact]
    public void Normalize_rejects_null() =>
        // ArgumentNullException derives from ArgumentException, so this is still a 400.
        Assert.Throws<ArgumentNullException>(() => PaymentMethods.Normalize(null!));

    [Theory]
    [InlineData("CARD", "card")]
    [InlineData("PROMPTPAY_QR", "promptpay")]
    [InlineData("INSTALLMENT", "installment")]
    public void ForChannel_maps_every_checkout_channel_to_its_method(string channel, string expected) =>
        // purchase-flow-completion REQ-6.1: the ONE translation from the purchase flow's channel wire value
        // to a payment method. Both the checkout eligibility gate and the customer pay path read it, so a
        // wrong pair here means a customer picks one channel and is charged on another.
        Assert.Equal(expected, PaymentMethods.ForChannel(channel));

    [Theory]
    [InlineData("card")]              // the method code, not a channel — the two vocabularies are separate
    [InlineData("promptpay_qr")]      // channels are case-sensitive enum member names
    [InlineData("PROMPTPAY")]
    [InlineData("QR")]
    [InlineData("")]
    public void ForChannel_rejects_a_value_outside_the_channel_contract(string channel) =>
        Assert.Throws<ArgumentException>(() => PaymentMethods.ForChannel(channel));

    [Fact]
    public void ForChannel_rejects_null() =>
        Assert.Throws<ArgumentNullException>(() => PaymentMethods.ForChannel(null!));

    [Theory]
    [InlineData("card", true)]
    [InlineData("CARD", true)]
    [InlineData(" promptpay ", true)]
    [InlineData("installment", true)]
    [InlineData("paypal", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsKnown_matches_the_vocabulary_case_and_whitespace_insensitively(string? input, bool expected) =>
        Assert.Equal(expected, PaymentMethods.IsKnown(input));
}
