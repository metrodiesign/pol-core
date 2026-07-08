using Tenant.Domain;

namespace Tenant.Tests;

public sealed class TenantCodeTests
{
    [Theory]
    [InlineData("vcommerce", "vcommerce")]
    [InlineData("VCOMMERCE", "vcommerce")]
    [InlineData("  vCommerce  ", "vcommerce")]
    public void Normalize_trims_and_lowercases(string raw, string expected) =>
        Assert.Equal(expected, TenantCode.Normalize(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_rejects_empty(string? raw) =>
        Assert.ThrowsAny<ArgumentException>(() => TenantCode.Normalize(raw!)); // null -> ArgumentNullException (subclass)

    [Theory]
    [InlineData("vprivilege")]
    [InlineData("vcommerce")]
    [InlineData("vsouvenir")]
    public void IsAllowed_true_for_captive_codes(string code) =>
        Assert.True(TenantCode.IsAllowed(code));

    [Theory]
    [InlineData("evilcorp")]
    [InlineData("vCommerce")] // not normalized -> not in the (lowercase) allowlist
    [InlineData("")]
    public void IsAllowed_false_otherwise(string code) =>
        Assert.False(TenantCode.IsAllowed(code));
}
