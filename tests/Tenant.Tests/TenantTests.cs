using Tenant.Domain;
using DomainTenant = Tenant.Domain.Tenant;

namespace Tenant.Tests;

public sealed class TenantTests
{
    private static readonly DateTime Now = new(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc);

    private static DomainTenant CreateValid(string code = "vcommerce", string currency = "THB") =>
        DomainTenant.Create(code, "vCommerce Co., Ltd.", "0105560000000", "TH", currency,
            ["card", "promptpay"], """{"branding":{"statementName":"VCOMMERCE"}}""", Now);

    [Fact]
    public void Create_valid_tenant_is_active_with_normalized_fields()
    {
        var t = CreateValid(code: "VCommerce", currency: "thb");

        Assert.NotEqual(Guid.Empty, t.Id);
        Assert.Equal("vcommerce", t.Code);          // normalized lowercase
        Assert.Equal("THB", t.Currency);            // normalized upper
        Assert.Equal("TH", t.Country);
        Assert.Equal(TenantStatus.Active, t.Status);
        Assert.Equal("card,promptpay", t.EnabledChannels);
        Assert.Equal(Now, t.CreatedAt);
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
        Assert.Throws<ArgumentException>(() => DomainTenant.Create(
            "vcommerce", displayName, "0105560000000", "TH", "THB", ["card"], null, Now));

    [Theory]
    [InlineData("Thailand")] // not alpha-2
    [InlineData("T")]
    public void Create_rejects_non_alpha2_country(string country) =>
        Assert.Throws<ArgumentException>(() => DomainTenant.Create(
            "vcommerce", "vCommerce", "0105560000000", country, "THB", ["card"], null, Now));

    [Fact]
    public void Create_with_null_channels_stores_empty_string()
    {
        var t = DomainTenant.Create("vsouvenir", "vSouvenir", "0105560000001", "TH", "THB", null, null, Now);

        Assert.Equal(string.Empty, t.EnabledChannels);
        Assert.Equal("{}", t.Metadata); // null metadata defaults to empty JSON object
    }
}
