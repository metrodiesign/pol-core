using Payments.Domain.Configuration;
using SharedKernel;

namespace Payments.Tests;

[Trait("Capability", "MerchantConfiguration")]
[Trait("Requirement", "REQ-5.9")]
public sealed class PaymentEligibilityPolicyTests
{
    private static PaymentEligibilityRequest Allowed() => new(
        "card",
        Money.Of(100m, "THB"),
        new HashSet<string>(["THB"], StringComparer.OrdinalIgnoreCase),
        MinimumAmount: 1m,
        MaximumAmount: 1000m,
        BusinessPolicyEnabled: true,
        MerchantEnabled: true,
        CreatorEnabled: true,
        ProviderAccountEnabled: true,
        AdapterCapabilityVerified: true,
        ProviderContractEvidence: true,
        EmergencyDisabled: false);

    [Theory]
    [InlineData("business", PaymentEligibilityDenial.BusinessPolicy)]
    [InlineData("merchant", PaymentEligibilityDenial.Merchant)]
    [InlineData("creator", PaymentEligibilityDenial.Creator)]
    [InlineData("account", PaymentEligibilityDenial.ProviderAccount)]
    [InlineData("adapter", PaymentEligibilityDenial.AdapterCapability)]
    [InlineData("contract", PaymentEligibilityDenial.ProviderContractEvidence)]
    [InlineData("emergency", PaymentEligibilityDenial.EmergencyDisabled)]
    public void Removing_any_intersection_factor_removes_the_method(
        string factor, PaymentEligibilityDenial expected)
    {
        var request = Allowed() with
        {
            BusinessPolicyEnabled = factor != "business",
            MerchantEnabled = factor != "merchant",
            CreatorEnabled = factor != "creator",
            ProviderAccountEnabled = factor != "account",
            AdapterCapabilityVerified = factor != "adapter",
            ProviderContractEvidence = factor != "contract",
            EmergencyDisabled = factor == "emergency",
        };

        var result = PaymentEligibilityPolicy.Evaluate(request);

        Assert.False(result.Allowed);
        Assert.Equal(expected, result.Denial);
    }

    [Fact]
    public void Amount_and_currency_are_part_of_the_same_intersection()
    {
        var wrongCurrency = PaymentEligibilityPolicy.Evaluate(Allowed() with
        {
            Amount = Money.Of(100m, "USD"),
        });
        var wrongAmount = PaymentEligibilityPolicy.Evaluate(Allowed() with
        {
            Amount = Money.Of(1001m, "THB"),
        });

        Assert.Equal(PaymentEligibilityDenial.Currency, wrongCurrency.Denial);
        Assert.Equal(PaymentEligibilityDenial.Amount, wrongAmount.Denial);
    }
}
