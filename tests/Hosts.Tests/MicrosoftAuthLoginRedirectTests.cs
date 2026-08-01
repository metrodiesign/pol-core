extern alias ApiHost;
using System.Net;
using BuildingBlocks.Infrastructure.Outbox;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Hosts.Tests;

// GET .../auth/microsoft/login hands off to the side's "AdminMicrosoft"/"MerchantUserMicrosoft" OIDC handler, which
// redirects to the Entra v2 authorize endpoint with Authorization Code + PKCE + state + nonce and only openid+email.
// A static OIDC Configuration is injected so the challenge builds the redirect WITHOUT a network metadata fetch.

file sealed class MicrosoftLoginFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string Tenant = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";
    public const string AdminClientId = "11111111-aaaa-aaaa-aaaa-111111111111";
    public const string MerchantClientId = "22222222-bbbb-bbbb-bbbb-222222222222";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        // ClientId/ClientSecret are read at service-registration time, so they must be host settings.
        builder.UseSetting("AdminAuth:Providers:Microsoft:Authority", $"https://login.microsoftonline.com/{Tenant}/v2.0");
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientId", AdminClientId);
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/microsoft/callback");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:Authority", "https://login.microsoftonline.com/organizations/v2.0");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", MerchantClientId);
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:CallbackPath", "/api/v1/merchants/auth/microsoft/callback");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["AdminSession:ReturnUrlAllowlist:0"] = "/dashboard",
                ["MerchantUser:Session:ReturnUrlAllowlist:0"] = "/dashboard",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // Static config -> the challenge builds the redirect without fetching the Entra discovery document.
            // Set the ConfigurationManager itself (not just Configuration): this test's PostConfigure runs AFTER the
            // framework's OpenIdConnectPostConfigureOptions, which has already installed a metadata-fetching manager —
            // Configuration alone would be ignored and the challenge would hit the (nonexistent) tenant's endpoint.
            foreach (var scheme in new[]
            {
                ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft",
                ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Microsoft",
            })
                services.PostConfigure<OpenIdConnectOptions>(scheme, options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = "https://login.microsoftonline.com/{tenantid}/v2.0",
                            AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                            TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                            JwksUri = $"https://login.microsoftonline.com/{Tenant}/discovery/v2.0/keys",
                        }));
        });
    }
}

public sealed class MicrosoftAuthLoginRedirectTests
{
    [Theory]
    [InlineData("/api/v1/admins/auth/microsoft/login?returnTo=/dashboard",
        MicrosoftLoginFactory.AdminClientId, "/api/v1/admins/auth/microsoft/callback")]
    [InlineData("/api/v1/merchants/auth/microsoft/login?returnTo=/dashboard",
        MicrosoftLoginFactory.MerchantClientId, "/api/v1/merchants/auth/microsoft/callback")]
    public async Task Microsoft_login_redirects_to_entra_authorize_with_code_pkce_and_the_provider_scoped_callback(
        string loginPath, string clientId, string callbackPath)
    {
        using var factory = new MicrosoftLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(loginPath);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("login.microsoftonline.com", location.Host);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(clientId, query["client_id"]);
        Assert.Equal("openid email profile", query["scope"].ToString()); // profile is REQUIRED: oid/tid ride on it
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.EndsWith(callbackPath, query["redirect_uri"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Id_token_validation_uses_the_explicit_two_minute_clock_skew()
    {
        // Explicit, not the library's 5-minute default — set in both (deliberately duplicated) OIDC wirings.
        using var factory = new MicrosoftLoginFactory();
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        foreach (var scheme in new[]
        {
            ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft",
            ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Microsoft",
        })
            Assert.Equal(TimeSpan.FromMinutes(2), monitor.Get(scheme).TokenValidationParameters.ClockSkew);
    }
}
