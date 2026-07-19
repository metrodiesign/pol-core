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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Hosts.Tests;

// GET /api/v1/admins/auth/login hands off to the OIDC handler, which redirects to Google's authorize endpoint with the
// Authorization Code + PKCE + state + nonce parameters and only the openid+email scope (REQ-1.1/1.5). A static
// OIDC Configuration is injected so the challenge builds the redirect WITHOUT a network metadata fetch.

file sealed class LoginFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string ClientId = "admin-oidc-client.apps.googleusercontent.com";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        // Dev-convenience auto-migrate (Program.cs) reads this key too; blank it so a developer's real local
        // appsettings.Development.json Migrator connection can never make this "no live DB" test touch one.
        builder.UseSetting("ConnectionStrings:Migrator", "");
        // ClientId/ClientSecret are read at service-registration time (AddAdminOidcAuthentication), so they must
        // be host settings, not late-layered app config.
        builder.UseSetting("Google:Oidc:ClientId", ClientId);
        builder.UseSetting("Google:Oidc:ClientSecret", "test-secret");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:App"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["ConnectionStrings:Admin"] = "Server=(local);Database=pol_test;Trusted_Connection=True;",
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["AdminSession:ReturnUrlAllowlist:0"] = "/",
                ["AdminSession:ReturnUrlAllowlist:1"] = "/dashboard",
            });
        });
        builder.ConfigureServices(services =>
        {
            // The real key ring persists to SQL via the keyed pol_admin context; tests have no DB, so use an
            // in-memory ephemeral provider for the OIDC correlation/nonce cookies.
            services.AddDataProtection().UseEphemeralDataProtectionProvider();

            // Static config -> the challenge builds the redirect without fetching Google's discovery document.
            services.PostConfigure<OpenIdConnectOptions>(ApiHost::Api.Admins.OidcAuthentication.Scheme, options =>
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

        var response = await client.GetAsync("/api/v1/admins/auth/login?returnTo=/dashboard");

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
        Assert.EndsWith("/api/v1/admins/auth/callback", query["redirect_uri"].ToString(), StringComparison.Ordinal); // REQ-6.2: challenge targets the NEW callback
    }

    [Fact]
    public async Task Login_sets_a_dp_protected_correlation_cookie_for_the_pre_auth_state()
    {
        using var factory = new LoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/admins/auth/login");

        // The OIDC handler persists state/nonce in a correlation + nonce cookie (REQ-1.2).
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.Contains("Correlation", StringComparison.OrdinalIgnoreCase) || c.Contains("Nonce", StringComparison.OrdinalIgnoreCase));
    }

    // Guards against the section-name mismatch bugfix regressing (AdminAuthOptions.cs): PlatformUserSessionOptions
    // must bind from the "AdminSession" section (matches the committed appsettings.json key), not the old
    // "Session" section that section-name value never matched. The allowlist above is set via the
    // factory's own in-memory config (not a gitignored appsettings.Development.json) so this test is
    // self-contained in a clean checkout/CI.
    [Fact]
    public void PlatformUserSessionOptions_bind_from_the_appsettings_AdminSession_section()
    {
        using var factory = new LoginFactory();

        var options = factory.Services.GetRequiredService<IOptions<ApiHost::Api.Admins.AdminSessionOptions>>().Value;

        // NotEmpty + Contains (not a full-list Equal): a machine's own gitignored appsettings.Development.json
        // may add further allowlist entries on top of these, and this test must stay green either way — only
        // the regression (SectionName drifts, section never binds, allowlist silently comes back empty) should fail it.
        Assert.NotEmpty(options.ReturnUrlAllowlist);
        Assert.Contains("/dashboard", options.ReturnUrlAllowlist);
    }
}
