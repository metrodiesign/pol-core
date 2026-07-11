using Merchants.Domain;

namespace Merchants.Tests;

public sealed class MerchantTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    private static Merchant CreateValid(string code = "vcommerce", string currency = "THB") =>
        Merchant.Create(code, "vCommerce Co., Ltd.", "0105560000000", "TH", currency,
            ["card", "promptpay"], """{"branding":{"statementName":"VCOMMERCE"}}""", Now);

    [Fact]
    public void Create_valid_merchant_is_active_with_normalized_fields()
    {
        var m = CreateValid(code: "VCommerce", currency: "thb");

        Assert.NotEqual(Guid.Empty, m.Id);
        Assert.Equal("vcommerce", m.Code);          // normalized lowercase
        Assert.Equal("THB", m.Currency);            // normalized upper
        Assert.Equal("TH", m.Country);
        Assert.Equal(MerchantStatus.Active, m.Status);
        Assert.Equal("card,promptpay", m.EnabledChannels);
        Assert.Equal(Now, m.CreatedAt);
    }

    [Fact]
    public void Create_rejects_code_not_in_allowlist() =>
        Assert.Throws<ArgumentException>(() => CreateValid(code: "evilcorp"));

    [Fact]
    public void Create_rejects_unsupported_currency() =>
        Assert.Throws<ArgumentException>(() => CreateValid(currency: "EUR"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_rejects_blank_display_name(string displayName) =>
        Assert.Throws<ArgumentException>(() => Merchant.Create(
            "vcommerce", displayName, "0105560000000", "TH", "THB", ["card"], null, Now));

    [Theory]
    [InlineData("Thailand")] // not alpha-2
    [InlineData("T")]
    public void Create_rejects_non_alpha2_country(string country) =>
        Assert.Throws<ArgumentException>(() => Merchant.Create(
            "vcommerce", "vCommerce", "0105560000000", country, "THB", ["card"], null, Now));

    [Fact]
    public void Create_with_null_channels_stores_empty_string()
    {
        var m = Merchant.Create("vsouvenir", "vSouvenir", "0105560000001", "TH", "THB", null, null, Now);

        Assert.Equal(string.Empty, m.EnabledChannels);
        Assert.Equal("{}", m.Metadata); // null metadata defaults to empty JSON object
    }
}
