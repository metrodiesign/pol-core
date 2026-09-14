extern alias ApiHost;

namespace Hosts.Tests;

public sealed class WorkforceAuthorityTests
{
    private const string Tenant = "3F2504E0-4F89-41D3-9A0C-0305E82C3301";

    [Theory]
    [InlineData("https://login.microsoftonline.com/3F2504E0-4F89-41D3-9A0C-0305E82C3301/v2.0")]
    [InlineData("HTTPS://LOGIN.MICROSOFTONLINE.COM:443/3F2504E0-4F89-41D3-9A0C-0305E82C3301/v2.0/")]
    public void Valid_public_cloud_authority_returns_canonical_tenant(string authority)
    {
        var tenantId = ApiHost::Api.IdentityAccess.WorkforceAuthority.Parse(authority);

        Assert.Equal(Guid.Parse(Tenant), tenantId);
        Assert.Equal("3f2504e0-4f89-41d3-9a0c-0305e82c3301", tenantId.ToString("D"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("http://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.com:444/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://user@login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.us/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://example.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://tenant.ciamlogin.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.com/common/v2.0")]
    [InlineData("https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0")]
    [InlineData("https://login.microsoftonline.com/3f2504e04f8941d39a0c0305e82c3301/v2.0")]
    [InlineData("https://login.microsoftonline.com/{3f2504e0-4f89-41d3-9a0c-0305e82c3301}/v2.0")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0/extra")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/V2.0")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0//")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0?x=1")]
    [InlineData("https://login.microsoftonline.com/3f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0#x")]
    [InlineData("https://login.microsoftonline.com/%33f2504e0-4f89-41d3-9a0c-0305e82c3301/v2.0")]
    public void Invalid_or_non_workforce_authority_fails_without_echoing_value(string authority)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ApiHost::Api.IdentityAccess.WorkforceAuthority.Parse(authority));

        Assert.Contains("IdentityAccess:Workforce:Authority", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("3f2504e0-4f89-41d3-9a0c-0305e82c3301", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
