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
file sealed class AgentLoginFactory(bool includeEndSessionEndpoint = true) : WebApplicationFactory<ApiHost::Program>
{
    public const string AgentTenant = "agent-tenant";
    public const string AgentClientId = "agent-entra-client";
    public const string AgentAuthority = "https://agent.login.test/agent-tenant/v2.0";
    public const string AgentWebApp = "https://agent-spa.task2.test";
    public static readonly Guid AgentMerchant = Guid.Parse("e1000000-0000-4000-8000-0000000000e1");
    public const string LegacyMerchantClientId = "22222222-bbbb-bbbb-bbbb-222222222222";
    public const string LegacyMerchantWebApp = "https://legacy-spa.task2.test";

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
        builder.UseSetting("IdentityAccess:AgentIssuer", AgentAuthority);
        builder.UseSetting("IdentityAccess:AgentTenantId", AgentTenant);
        builder.UseSetting("IdentityAccess:AgentAudience", AgentClientId);
        builder.UseSetting("IdentityAccess:AgentMerchantId", AgentMerchant.ToString("D"));
        builder.UseSetting("IdentityAccess:AgentWebAppBaseUrl", AgentWebApp);
        // The legacy merchant-user scheme shares the agent callback path (the only redirect URI on the Entra app).
        builder.UseSetting("MerchantAuth:Providers:Microsoft:Authority",
            "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientId", LegacyMerchantClientId);
        builder.UseSetting("MerchantAuth:Providers:Microsoft:ClientSecret", "legacy-secret");
        builder.UseSetting("MerchantAuth:Providers:Microsoft:CallbackPath", ApiIdentity.IdentityAccessWiring.AgentCallbackPath);
        builder.UseSetting("MerchantSession:WebAppBaseUrl", LegacyMerchantWebApp);
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
            services.PostConfigure<OpenIdConnectOptions>(
                ApiHost::Api.Merchants.UserOidcAuthentication.SchemePrefix + "Microsoft", options =>
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                        new OpenIdConnectConfiguration
                        {
                            Issuer = "https://1aee3cad-1e4d-4de5-9e25-424d0d12520b.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/v2.0",
                            AuthorizationEndpoint = "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/oauth2/v2.0/authorize",
                            TokenEndpoint = "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/oauth2/v2.0/token",
                            JwksUri = "https://viriyahexternal.ciamlogin.com/1aee3cad-1e4d-4de5-9e25-424d0d12520b/discovery/v2.0/keys",
                        }));
            services.PostConfigure<OpenIdConnectOptions>("IdentityAgentMicrosoft", options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration
                    {
                        Issuer = AgentAuthority,
                        AuthorizationEndpoint = "https://agent.login.test/oauth2/v2.0/authorize",
                        TokenEndpoint = "https://agent.login.test/oauth2/v2.0/token",
                        JwksUri = "https://agent.login.test/discovery/v2.0/keys",
                        EndSessionEndpoint = includeEndSessionEndpoint
                            ? "https://agent.login.test/agent-tenant/oauth2/v2.0/logout"
                            : null,
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
        Assert.EndsWith(ApiIdentity.IdentityAccessWiring.AgentCallbackPath, query["redirect_uri"].ToString(), StringComparison.Ordinal);
        Assert.False(query.ContainsKey("prompt"));

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
    public async Task Agent_select_account_prompt_is_forwarded_to_ciam()
    {
        using var factory = new AgentLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(AuthorizeUrl("agent-challenge", "select_account"));

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal("select_account", query["prompt"]);
    }

    [Fact]
    public async Task Agent_select_account_prompt_does_not_reuse_a_temporary_login_cookie()
    {
        using var factory = new AgentLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, AuthorizeUrl("agent-challenge", "select_account"));
        request.Headers.Add("Cookie", EmployeeTokenFlow.LoginCookie(factory.Services, Guid.CreateVersion7()));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("agent.login.test", response.Headers.Location!.Host);
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        Assert.Equal("select_account", query["prompt"]);
    }

    [Fact]
    public async Task Agent_prompt_outside_the_allowlist_is_rejected_as_invalid_request()
    {
        using var factory = new AgentLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(AuthorizeUrl("agent-challenge", "login"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_ciam_logout_uses_metadata_and_the_server_owned_post_logout_uri()
    {
        using var factory = new AgentLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(
            "/api/v1/auth/agents/logout?returnTo=https%3A%2F%2Fevil.test&post_logout_redirect_uri=https%3A%2F%2Fevil.test");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("agent.login.test", response.Headers.Location!.Host);
        Assert.Equal("/agent-tenant/oauth2/v2.0/logout", response.Headers.Location.AbsolutePath);
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        Assert.Equal(AgentLoginFactory.AgentWebApp + "/login", query["post_logout_redirect_uri"]);
        Assert.DoesNotContain("evil.test", response.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("token", response.Headers.Location.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Agent_ciam_logout_fails_closed_without_an_end_session_endpoint()
    {
        using var factory = new AgentLoginFactory(includeEndSessionEndpoint: false);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/v1/auth/agents/logout");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("end_session_not_configured", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task A_legacy_merchant_callback_on_the_shared_path_is_passed_through_by_the_agent_scheme()
    {
        using var factory = new AgentLoginFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var start = await client.GetAsync("/api/v1/merchants/auth/microsoft/login");
        Assert.Equal(HttpStatusCode.Found, start.StatusCode);
        var query = QueryHelpers.ParseQuery(start.Headers.Location!.Query);
        Assert.Equal(AgentLoginFactory.LegacyMerchantClientId, query["client_id"]);
        Assert.EndsWith(ApiIdentity.IdentityAccessWiring.AgentCallbackPath, query["redirect_uri"].ToString(), StringComparison.Ordinal);

        // The provider bounces back with the legacy scheme's state. The agent scheme is registered first on the
        // same path and cannot unprotect that state, so it must pass the request on to the legacy scheme instead
        // of failing it to the agent SPA error page.
        var callback = new HttpRequestMessage(HttpMethod.Get,
            ApiIdentity.IdentityAccessWiring.AgentCallbackPath + "?error=access_denied&state=" + Uri.EscapeDataString(query["state"]!));
        if (start.Headers.TryGetValues("Set-Cookie", out var cookies))
            callback.Headers.Add("Cookie", string.Join("; ", cookies.Select(cookie => cookie.Split(';')[0])));
        var response = await client.SendAsync(callback);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(AgentLoginFactory.LegacyMerchantWebApp + "/login-error?reason=access-denied",
            response.Headers.Location!.ToString());
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

            using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
            logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var logoutResponse = await client.SendAsync(logout);
            Assert.Equal(HttpStatusCode.NoContent, logoutResponse.StatusCode);

            using var revokedMe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me");
            revokedMe.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            using var revokedMeResponse = await client.SendAsync(revokedMe);
            Assert.Equal(HttpStatusCode.Unauthorized, revokedMeResponse.StatusCode);
        }
        finally
        {
            await CleanupAsync(accountId);
        }
    }

    private static string AuthorizeUrl(string challenge, string? prompt = null) => QueryHelpers.AddQueryString(
        "/oauth/authorize",
        new Dictionary<string, string?>
        {
            ["client_id"] = AgentSpaClientId,
            ["redirect_uri"] = AgentRedirectUri,
            ["response_type"] = "code",
            ["scope"] = "openid offline_access",
            ["state"] = "agent-state",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = prompt,
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
