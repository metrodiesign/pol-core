extern alias ApiHost;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Integration.Tests;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

/// <summary>Host with BOTH human providers configured so /oauth/authorize can pick the agent one from client_id.</summary>
file sealed class AgentLoginFactory : WebApplicationFactory<ApiHost::Program>
{
    public const string AgentTenant = "agent-tenant";
    public const string AgentClientId = "agent-entra-client";
    public const string AgentAuthority = "https://agent.login.test/agent-tenant/v2.0";
    public const string AgentWebApp = "https://agent-spa.task2.test";
    public static readonly Guid AgentMerchant = Guid.Parse("e1000000-0000-4000-8000-0000000000e1");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", IntegrationDb.AppConn);
        builder.UseSetting("ConnectionStrings:Admin", IntegrationDb.AppConn);
        builder.UseSetting("OAuth:Issuer", "https://oauth.task2.test");
        builder.UseSetting("IdentityAccess:WorkforceWebAppBaseUrl", "https://spa.task2.test");
        builder.UseSetting("IdentityAccess:Agent:Authority", AgentAuthority);
        builder.UseSetting("IdentityAccess:Agent:ClientId", AgentClientId);
        builder.UseSetting("IdentityAccess:Agent:ClientSecret", "test-secret");
        builder.UseSetting("IdentityAccess:Agent:CallbackPath", "/api/v1/auth/agents/callback");
        builder.UseSetting("IdentityAccess:AgentIssuer", AgentAuthority);
        builder.UseSetting("IdentityAccess:AgentTenantId", AgentTenant);
        builder.UseSetting("IdentityAccess:AgentAudience", AgentClientId);
        builder.UseSetting("IdentityAccess:AgentMerchantId", AgentMerchant.ToString("D"));
        builder.UseSetting("IdentityAccess:AgentWebAppBaseUrl", AgentWebApp);
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
            services.PostConfigure<OpenIddictServerAspNetCoreOptions>(
                options => options.DisableTransportSecurityRequirement = true);
            services.PostConfigure<OpenIdConnectOptions>("IdentityAgentMicrosoft", options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration
                    {
                        Issuer = AgentAuthority,
                        AuthorizationEndpoint = "https://agent.login.test/oauth2/v2.0/authorize",
                        TokenEndpoint = "https://agent.login.test/oauth2/v2.0/token",
                        JwksUri = "https://agent.login.test/discovery/v2.0/keys",
                    }));
        });
    }
}

[Trait("Capability", "IdentityAccess")]
[Trait("Category", "Integration")]
public sealed class AgentLoginTests
{
    private const string AgentSpaClientId = "pol-merchant";
    private static readonly string AgentRedirectUri =
        AgentLoginFactory.AgentWebApp + ApiIdentity.WorkforceClientRegistration.CallbackPath;

    [Fact]
    public async Task Agent_client_authorize_without_a_login_cookie_challenges_the_agent_provider()
    {
        using var factory = new AgentLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var authorizeUrl = AuthorizeUrl("agent-challenge");
        var response = await client.GetAsync(authorizeUrl);

        Assert.True(response.StatusCode == HttpStatusCode.Found, await response.Content.ReadAsStringAsync());
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("agent.login.test", response.Headers.Location.Host);
        Assert.Equal(AgentLoginFactory.AgentClientId, query["client_id"]);
        Assert.EndsWith("/api/v1/auth/agents/callback", query["redirect_uri"].ToString(), StringComparison.Ordinal);

        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get("IdentityAgentMicrosoft");
        var properties = options.StateDataFormat.Unprotect(query["state"]!);
        Assert.NotNull(properties);
        Assert.Equal(authorizeUrl, properties!.RedirectUri);
        Assert.Equal("external", properties.Items["identity.realm"]);
        Assert.Equal(AgentLoginFactory.AgentAuthority, properties.Items["identity.expected_issuer"]);
        Assert.Equal(AgentLoginFactory.AgentMerchant.ToString("D"), properties.Items["identity.merchant_id"]);
    }

    [Fact]
    public async Task Approved_agent_gets_a_code_for_the_agent_client_only()
    {
        var accountId = Guid.CreateVersion7();
        await SeedAgentAsync(accountId);
        try
        {
            using var factory = new AgentLoginFactory();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            // The workforce client must not accept the agent's login cookie (cross-realm code minting).
            await EmployeeTokenFlow.EnsureClientAsync(factory.Services);
            using var crossRealm = new HttpRequestMessage(HttpMethod.Get,
                EmployeeTokenFlow.AuthorizeUrl("cross-realm", EmployeeTokenFlow.RedirectUriFor(factory.Services)));
            crossRealm.Headers.Add("Cookie", EmployeeTokenFlow.LoginCookie(factory.Services, accountId));
            using var crossRealmResponse = await client.SendAsync(crossRealm);
            Assert.Equal(HttpStatusCode.Forbidden, crossRealmResponse.StatusCode);

            var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            using var authorize = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl(challenge));
            authorize.Headers.Add("Cookie", EmployeeTokenFlow.LoginCookie(factory.Services, accountId));
            using var authorizeResponse = await client.SendAsync(authorize);
            Assert.True(authorizeResponse.StatusCode == HttpStatusCode.Found,
                $"{(int)authorizeResponse.StatusCode}: {await authorizeResponse.Content.ReadAsStringAsync()}");
            var location = authorizeResponse.Headers.Location!.ToString();
            Assert.StartsWith(AgentRedirectUri, location, StringComparison.Ordinal);
            var query = QueryHelpers.ParseQuery(authorizeResponse.Headers.Location.Query);

            using var token = await EmployeeTokenFlow.TokenAsync(client, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = query["code"].ToString(),
                ["code_verifier"] = verifier,
                ["redirect_uri"] = AgentRedirectUri,
                ["client_id"] = AgentSpaClientId,
            });
            var body = await token.Content.ReadAsStringAsync();
            Assert.True(token.StatusCode == HttpStatusCode.OK, $"{(int)token.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            var accessToken = json.RootElement.GetProperty("access_token").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("refresh_token").GetString()));

            using var me = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var meResponse = await client.SendAsync(me);
            Assert.True(meResponse.StatusCode == HttpStatusCode.OK, await meResponse.Content.ReadAsStringAsync());
        }
        finally
        {
            await CleanupAsync(accountId);
        }
    }

    private static string AuthorizeUrl(string challenge) => QueryHelpers.AddQueryString("/oauth/authorize",
        new Dictionary<string, string?>
        {
            ["client_id"] = AgentSpaClientId,
            ["redirect_uri"] = AgentRedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid offline_access",
            ["state"] = "agent-state",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

    /// <summary>What ApproveAsync leaves behind for the login path: an Active Agent account with its external login.</summary>
    private static async Task SeedAgentAsync(Guid accountId)
    {
        await using var seed = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(seed, """
            INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
            VALUES (@account, 2, @name, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT acct.LoginAccounts (Id, AccountId, Provider, TenantId, ExternalUserId, Email, DisplayName)
            VALUES (NEWID(), @account, N'microsoft', @tenant, @external, @email, @name);
            """, ("@account", accountId), ("@name", "Approved agent"), ("@tenant", AgentLoginFactory.AgentTenant),
            ("@external", $"oid-{accountId:N}"), ("@email", $"agent-{accountId:N}@example.test"));
    }

    private static async Task CleanupAsync(Guid accountId)
    {
        await using var db = await IntegrationDb.OpenAsync(IntegrationDb.SaConn);
        await IntegrationDb.ExecAsync(db, """
            DELETE FROM acct.LoginAccounts WHERE AccountId=@account;
            DELETE FROM acct.Accounts WHERE Id=@account;
            """, ("@account", accountId));
    }
}
