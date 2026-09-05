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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Hosts.Tests;

// GET /api/v1/merchants/auth/microsoft/login hands off to the "MerchantUserMicrosoft" OIDC handler, which
// redirects to the tenant-pinned CIAM authorize endpoint with the Authorization Code + PKCE + state + nonce
// parameters and only the openid+email+profile scope (REQ-8.1/8.4). A static OIDC Configuration is injected so
// the challenge builds the redirect WITHOUT a network metadata fetch.

file sealed class MerchantUserLoginFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string ClientId = "22222222-bbbb-bbbb-bbbb-222222222222";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        // ClientId/ClientSecret are read at service-registration time (AddMerchantUserOidcAuthentication), so they
        // must be host settings, not late-layered app config.
        builder.UseSetting("MerchantAuth:Providers:Microsoft:Authority",
            "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", ClientId);
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:CallbackPath", "/api/v1/merchants/auth/microsoft/callback");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.IgnoreMachineLocalDevelopmentSettings();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["MerchantSession:ReturnUrlAllowlist:0"] = "/",
                ["MerchantSession:ReturnUrlAllowlist:1"] = "/dashboard",
            });
        });
        builder.ConfigureServices(services =>
        {
            // The real key ring persists to SQL via the keyed pol_admin context; tests have no DB, so use an
            // in-memory ephemeral provider for the OIDC correlation/nonce cookies.
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // Static config -> the challenge builds the redirect without fetching the discovery document.
            services.PostConfigure<OpenIdConnectOptions>(ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Microsoft", options =>
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0",
                    AuthorizationEndpoint = "https://viriyahexternal.ciamlogin.com/oauth2/v2.0/authorize",
                    TokenEndpoint = "https://viriyahexternal.ciamlogin.com/oauth2/v2.0/token",
                    JwksUri = "https://viriyahexternal.ciamlogin.com/discovery/v2.0/keys",
                });
        });
    }
}

public sealed class MerchantUserAuthLoginRedirectTests
{
    [Fact]
    public async Task Login_redirects_to_the_authorize_endpoint_with_code_pkce_state_nonce_and_openid_email_scope()
    {
        using var factory = new MerchantUserLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/merchants/auth/microsoft/login?returnTo=/dashboard");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("viriyahexternal.ciamlogin.com", location.Host);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);                 // Authorization Code (REQ-8.1)
        Assert.Equal(MerchantUserLoginFactory.ClientId, query["client_id"]);
        Assert.Equal("openid email profile", query["scope"].ToString()); // no offline access (REQ-8.4)
        Assert.Equal("S256", query["code_challenge_method"]);         // PKCE S256 (REQ-8.1)
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.EndsWith("/api/v1/merchants/auth/microsoft/callback", query["redirect_uri"].ToString(), StringComparison.Ordinal); // provider-scoped callback
    }

    [Fact]
    public async Task An_unknown_or_retired_provider_login_404s_while_microsoft_still_challenges()
    {
        using var factory = new MerchantUserLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var unknown = await client.GetAsync("/api/v1/merchants/auth/github/login");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Google is retired: no scheme is ever registered for it, so its login is indistinguishable from an
        // unknown provider.
        var retired = await client.GetAsync("/api/v1/merchants/auth/google/login");
        Assert.Equal(HttpStatusCode.NotFound, retired.StatusCode);

        var microsoft = await client.GetAsync("/api/v1/merchants/auth/microsoft/login");
        Assert.Equal(HttpStatusCode.Found, microsoft.StatusCode);
    }

    [Fact]
    public async Task Login_sets_a_dp_protected_correlation_cookie_for_the_pre_auth_state()
    {
        using var factory = new MerchantUserLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/merchants/auth/microsoft/login");

        // The OIDC handler persists state/nonce in a correlation + nonce cookie under the MerchantUserMicrosoft
        // scheme's own DP purpose (REQ-8.2/14.4).
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.Contains("Correlation", StringComparison.OrdinalIgnoreCase) || c.Contains("Nonce", StringComparison.OrdinalIgnoreCase));
    }
}
