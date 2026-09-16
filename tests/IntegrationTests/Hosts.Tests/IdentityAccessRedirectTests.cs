extern alias ApiHost;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

[Trait("Capability", "IdentityAccess")]
public sealed class IdentityAccessRedirectTests
{
    [Theory]
    [InlineData("/dashboard", "https://admin.example.test", "https://admin.example.test/dashboard")]
    [InlineData("/dashboard", "https://admin.example.test/", "https://admin.example.test/dashboard")]
    [InlineData("/", "https://admin.example.test", "https://admin.example.test/")]
    [InlineData("/dashboard", "", "/dashboard")]
    [InlineData(null, "https://admin.example.test", "https://admin.example.test/")]
    [InlineData("", "", "/")]
    [InlineData("//evil.test/x", "https://admin.example.test", "https://admin.example.test/")]
    [InlineData("/\\evil.test/x", "", "/")]
    [InlineData("https://evil.test/x", "https://admin.example.test", "https://admin.example.test/")]
    public void Post_login_redirect_lands_on_the_web_app_origin_for_same_origin_paths_only(
        string? returnTo, string webAppBaseUrl, string expected)
    {
        Assert.Equal(expected, ApiIdentity.IdentityBffLoginService.ToWebApp(returnTo, webAppBaseUrl));
    }

    [Theory]
    [InlineData("localhost:3001")]
    [InlineData("ftp://admin.example.test")]
    [InlineData("/relative")]
    public void Web_app_base_url_must_be_an_absolute_http_origin(string value)
    {
        var options = new ApiIdentity.IdentityAccessOptions { WorkforceWebAppBaseUrl = value };

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("WorkforceWebAppBaseUrl", error.Message);
    }

    [Theory]
    [InlineData("https://agent.example.test/path")]
    [InlineData("https://agent.example.test?next=/login")]
    [InlineData("https://user@agent.example.test")]
    public void Agent_web_app_base_url_must_be_an_origin_without_browser_control(string value)
    {
        var options = new ApiIdentity.IdentityAccessOptions { AgentWebAppBaseUrl = value };

        var error = Assert.Throws<InvalidOperationException>(() => options.Validate());
        Assert.Contains("AgentWebAppBaseUrl", error.Message);
    }

    [Fact]
    public void Agent_web_app_base_url_is_required_when_the_agent_provider_is_configured()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new ApiIdentity.IdentityAccessOptions().Validate(agentProviderConfigured: true));

        Assert.Contains("AgentWebAppBaseUrl", error.Message);
    }

    [Fact]
    public void Blank_web_app_base_urls_are_valid_when_their_providers_are_disabled()
    {
        new ApiIdentity.IdentityAccessOptions().Validate();
    }
}
