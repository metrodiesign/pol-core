extern alias ApiHost;
using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Hosts.Tests;

file sealed class IdentityAccessLoginFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string Tenant = "task2-tenant";
    public const string ClientId = "task2-workforce-client";
    public const string Authority = "https://login.task2.test/task2-tenant/v2.0";
    public const string WebApp = "https://spa.task2.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", Task2AppConnection());
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("IdentityAccess:Workforce:Authority", Authority);
        builder.UseSetting("IdentityAccess:Workforce:ClientId", ClientId);
        builder.UseSetting("IdentityAccess:Workforce:ClientSecret", "test-secret");
        builder.UseSetting("IdentityAccess:Workforce:CallbackPath", "/api/v1/auth/employees/callback");
        builder.UseSetting("IdentityAccess:WorkforceIssuer", Authority);
        builder.UseSetting("IdentityAccess:WorkforceTenantId", Tenant);
        builder.UseSetting("IdentityAccess:WorkforceAudience", ClientId);
        builder.UseSetting("IdentityAccess:WorkforceWebAppBaseUrl", WebApp);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.IgnoreMachineLocalDevelopmentSettings();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.PostConfigure<OpenIddict.Server.AspNetCore.OpenIddictServerAspNetCoreOptions>(
                options => options.DisableTransportSecurityRequirement = true);
            services.PostConfigure<OpenIdConnectOptions>("IdentityWorkforceMicrosoft", options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration
                    {
                        Issuer = Authority,
                        AuthorizationEndpoint = "https://login.task2.test/oauth2/v2.0/authorize",
                        TokenEndpoint = "https://login.task2.test/oauth2/v2.0/token",
                        JwksUri = "https://login.task2.test/discovery/v2.0/keys",
                    }));
        });
    }

    private static string Task2AppConnection()
    {
        var server = Environment.GetEnvironmentVariable("POL_SQL_SERVER");
        var password = Environment.GetEnvironmentVariable("POL_APP_PASSWORD");
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(password))
            return "Server=(local);Database=pol_test;Trusted_Connection=True;";
        var database = Environment.GetEnvironmentVariable("POL_DB") ?? "PolIdentityAccessTask2Test";
        return $"Server={server};Database={database};User Id=pol_app;Password={password};Encrypt=True;TrustServerCertificate=True;Pooling=False";
    }
}

[Trait("Capability", "IdentityAccess")]
[Trait("Category", "Integration")]
public sealed class IdentityAccessLoginTests
{
    [Fact]
    [Trait("Requirement", "REQ-2.6")]
    [Trait("Requirement", "REQ-2.11")]
    public async Task Employee_login_uses_code_pkce_state_nonce_and_single_oidc_state_owner()
    {
        using var factory = new IdentityAccessLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The SPA starts at /oauth/authorize (code + PKCE); without the login cookie the API parks the request
        // in the Entra challenge and returns to the same authorize URL after the callback.
        var authorizeUrl = EmployeeTokenFlow.AuthorizeUrl("login-challenge", $"{IdentityAccessLoginFactory.WebApp}/auth/callback");
        var response = await client.GetAsync(authorizeUrl);

        Assert.True(response.StatusCode == HttpStatusCode.Found, await response.Content.ReadAsStringAsync());
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("login.task2.test", response.Headers.Location.Host);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(IdentityAccessLoginFactory.ClientId, query["client_id"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.False(string.IsNullOrWhiteSpace(query["state"]));
        Assert.False(string.IsNullOrWhiteSpace(query["nonce"]));
        Assert.EndsWith("/api/v1/auth/employees/callback", query["redirect_uri"].ToString(), StringComparison.Ordinal);

        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get("IdentityWorkforceMicrosoft");
        var properties = options.StateDataFormat.Unprotect(query["state"]!);
        Assert.NotNull(properties);
        Assert.Equal(authorizeUrl, properties!.RedirectUri);
        Assert.Equal("workforce", properties.Items["identity.realm"]);
    }

    [Theory]
    [InlineData("access_denied", "access-denied")]
    [InlineData("server_error", "auth-failed")]
    public async Task Provider_failure_at_the_callback_redirects_to_the_web_app_error_page(
        string providerError, string expectedReason)
    {
        using var factory = new IdentityAccessLoginFactory();
        // https base: the OIDC correlation cookie is Secure, so the client only replays it on an https origin.
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost"),
        });
        var login = await client.GetAsync(EmployeeTokenFlow.AuthorizeUrl(
            "login-challenge", $"{IdentityAccessLoginFactory.WebApp}/auth/callback"));
        Assert.True(login.StatusCode == HttpStatusCode.Found, await login.Content.ReadAsStringAsync());
        var state = QueryHelpers.ParseQuery(login.Headers.Location!.Query)["state"].ToString();

        var callback = await client.PostAsync(
            "/api/v1/auth/employees/callback",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["state"] = state,
                ["error"] = providerError,
                ["error_description"] = "provider rejected the request",
            }));

        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);
        Assert.Equal(
            $"{IdentityAccessLoginFactory.WebApp}/login-error?reason={expectedReason}",
            callback.Headers.Location!.ToString());
        Assert.DoesNotContain("provider rejected", callback.Headers.Location.ToString());
    }

}
