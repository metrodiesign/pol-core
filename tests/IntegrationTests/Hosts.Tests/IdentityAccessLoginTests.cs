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
public sealed class IdentityAccessLoginTests
{
    [Fact]
    [Trait("Requirement", "REQ-2.6")]
    [Trait("Requirement", "REQ-2.11")]
    public async Task Employee_login_uses_code_pkce_state_nonce_and_single_oidc_state_owner()
    {
        using var factory = new IdentityAccessLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/auth/employees/login?returnTo=/dashboard");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
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
        Assert.Equal("/dashboard", properties!.RedirectUri);
        Assert.Equal("workforce", properties.Items["identity.realm"]);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.14")]
    public void Every_identity_cookie_mutation_route_carries_the_bff_csrf_marker()
    {
        using var factory = new IdentityAccessLoginFactory();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

        foreach (var path in new[]
        {
            "/api/v1/auth/session/refresh",
            "/api/v1/auth/merchant-context",
            "/api/v1/auth/logout",
        })
        {
            var endpoint = Assert.Single(endpoints, item =>
                string.Equals(item.RoutePattern.RawText, path, StringComparison.Ordinal));
            Assert.NotNull(endpoint.Metadata.GetMetadata<ApiHost::Api.IdentityAccess.BffCsrfProtected>());
        }
    }
}
