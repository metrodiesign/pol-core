using Merchants.Domain;

namespace Merchants.Tests;

public sealed class MerchantCodeTests
{
    [Theory]
    [InlineData("vcommerce", "vcommerce")]
    [InlineData("VCOMMERCE", "vcommerce")]
    [InlineData("  vCommerce  ", "vcommerce")]
    public void Normalize_trims_and_lowercases(string raw, string expected) =>
        Assert.Equal(expected, MerchantCode.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_rejects_empty(string? raw) =>
        Assert.ThrowsAny<ArgumentException>(() => MerchantCode.Normalize(raw!)); // null -> ArgumentNullException (subclass)

    [Theory]
    [InlineData("vprivilege")]
    [InlineData("vcommerce")]
    [InlineData("vsouvenir")]
    public void IsAllowed_true_for_captive_codes(string code) =>
        Assert.True(MerchantCode.IsAllowed(code));

    [Theory]
    [InlineData("evilcorp")]
    [InlineData("vCommerce")] // not normalized -> not in the (lowercase) allowlist
    [InlineData("")]
    public void IsAllowed_false_otherwise(string code) =>
        Assert.False(MerchantCode.IsAllowed(code));
}
