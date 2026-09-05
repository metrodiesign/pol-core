extern alias ApiHost;

namespace Hosts.Tests;

public sealed class InvitationLinkTests
{
    [Fact]
    public void Smtp_link_uses_the_canonical_merchant_web_app_base_url()
    {
        var link = ApiHost::Api.Merchants.SmtpInvitationEmailSender.BuildLink(
            "https://merchant.example.com/",
            "a b");

        Assert.Equal("https://merchant.example.com/invite#token=a%20b", link);
    }
}
