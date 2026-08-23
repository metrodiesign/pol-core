extern alias ApiHost;
using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Hosts.Tests;

file sealed class WorkforceLoginFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string Tenant = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    public const string ClientId = "11111111-aaaa-aaaa-aaaa-111111111111";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("AdminAuth:Providers:Microsoft:Authority", $"https://login.microsoftonline.com/{Tenant}/v2.0");
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientId", ClientId);
        builder.UseSetting("AdminAuth:Providers:Microsoft:ClientSecret", "test-secret");
        builder.UseSetting("AdminAuth:Providers:Microsoft:CallbackPath", "/api/v1/admins/auth/microsoft/callback");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.IgnoreMachineLocalDevelopmentSettings();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                ["AdminSession:ReturnUrlAllowlist:0"] = "/",
                ["AdminSession:ReturnUrlAllowlist:1"] = "/dashboard",
                ["AdminSession:ReturnUrlAllowlist:2"] = "/scalar",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.RemoveAll<Admins.Application.Users.IWorkforceTenantBindingStore>();
            services.AddSingleton<Admins.Application.Users.IWorkforceTenantBindingStore>(
                new TestWorkforceTenantBindingStore());
            services.PostConfigure<OpenIdConnectOptions>(
                ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft", options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
                            AuthorizationEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize",
                            TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token",
                            JwksUri = $"https://login.microsoftonline.com/{Tenant}/discovery/v2.0/keys",
                        }));
        });
    }
}

/// <summary>Admin OIDC is Microsoft workforce-only.</summary>
public sealed class AdminAuthLoginRedirectTests
{
    [Fact]
    public async Task Logout_requires_authenticated_admin_session()
    {
        using var factory = new WorkforceLoginFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsync("/api/v1/admins/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_redirects_to_microsoft_authorize_with_code_pkce_state_nonce_and_workforce_scope()
    {
        using var factory = new WorkforceLoginFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/v1/admins/auth/microsoft/login?returnTo=/dashboard");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("login.microsoftonline.com", location.Host);

        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(WorkforceLoginFactory.ClientId, query["client_id"]);
        Assert.Equal("openid email profile", query["scope"].ToString());
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.Equal("select_account", query["prompt"].ToString());
        Assert.EndsWith("/api/v1/admins/auth/microsoft/callback", query["redirect_uri"].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_google_login_and_callback_return_404()
    {
        using var factory = new WorkforceLoginFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/admins/auth/google/login")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/admins/auth/google/callback")).StatusCode);
    }

    [Fact]
    public async Task Scalar_returnTo_is_preserved_in_protected_microsoft_oidc_state()
    {
        using var factory = new WorkforceLoginFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/v1/admins/auth/microsoft/login?returnTo=/scalar");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(ApiHost::Api.Admins.OidcAuthentication.SchemePrefix + "Microsoft");
        var properties = options.StateDataFormat.Unprotect(query["state"]!);

        Assert.NotNull(properties);
        Assert.Equal("/scalar", properties!.RedirectUri);
        Assert.Equal("/scalar", properties.Items[ApiHost::Api.Admins.OidcAuthentication.ReturnToPropertyKey]);
    }

    [Fact]
    public async Task Microsoft_login_sets_a_dp_protected_correlation_cookie()
    {
        using var factory = new WorkforceLoginFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync("/api/v1/admins/auth/microsoft/login");

        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.Contains("Correlation", StringComparison.OrdinalIgnoreCase)
                || cookie.Contains("Nonce", StringComparison.OrdinalIgnoreCase));
    }
}
