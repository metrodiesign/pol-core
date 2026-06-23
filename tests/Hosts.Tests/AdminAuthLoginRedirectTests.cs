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

// GET /admin/auth/login hands off to the OIDC handler, which redirects to Google's authorize endpoint with the
// Authorization Code + PKCE + state + nonce parameters and only the openid+email scope (REQ-1.1/1.5). A static
// OIDC Configuration is injected so the challenge builds the redirect WITHOUT a network metadata fetch.

file sealed class LoginFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string ClientId = "admin-oidc-client.apps.googleusercontent.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("Google:Audiences:tenant", "test-client-id.apps.googleusercontent.com");
        // ClientId/ClientSecret are read at service-registration time (AddAdminOidcAuthentication), so they must
        // be host settings, not late-layered app config.
        builder.UseSetting("Google:Oidc:ClientId", ClientId);
        builder.UseSetting("Google:Oidc:ClientSecret", "test-secret");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Producer"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["AdminSession:ReturnUrlAllowlist:0"] = "/dashboard",
            });
        });
        builder.ConfigureServices(services =>
        {
            var dispatcher = services.SingleOrDefault(d =>
                d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxDispatcher));
            if (dispatcher is not null)
                services.Remove(dispatcher);

            // The real key ring persists to SQL via the keyed pol_admin context; tests have no DB, so use an
            // in-memory ephemeral provider for the OIDC correlation/nonce cookies.
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // Static config -> the challenge builds the redirect without fetching Google's discovery document.
            services.PostConfigure<OpenIdConnectOptions>(ApiHost::Api.AdminOidcAuthentication.Scheme, options =>
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = "https://accounts.google.com",
                    AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                    TokenEndpoint = "https://oauth2.googleapis.com/token",
                    JwksUri = "https://www.googleapis.com/oauth2/v3/certs",
                });
        });
    }
}

public sealed class AdminAuthLoginRedirectTests
{
    [Fact]
    public async Task Login_redirects_to_google_authorize_with_code_pkce_state_nonce_and_openid_email_scope()
    {
        using var factory = new LoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/admin/auth/login?returnTo=/dashboard");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("accounts.google.com", location.Host);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);                 // Authorization Code (REQ-1.1)
        Assert.Equal(LoginFactory.ClientId, query["client_id"]);
        Assert.Equal("openid email", query["scope"].ToString());      // only openid+email, no offline (REQ-1.5)
        Assert.Equal("S256", query["code_challenge_method"]);         // PKCE S256 (REQ-1.1)
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
    }

    [Fact]
    public async Task Login_sets_a_dp_protected_correlation_cookie_for_the_pre_auth_state()
    {
        using var factory = new LoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/admin/auth/login");

        // The OIDC handler persists state/nonce in a correlation + nonce cookie (REQ-1.2).
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.Contains("Correlation", StringComparison.OrdinalIgnoreCase) || c.Contains("Nonce", StringComparison.OrdinalIgnoreCase));
    }
}
