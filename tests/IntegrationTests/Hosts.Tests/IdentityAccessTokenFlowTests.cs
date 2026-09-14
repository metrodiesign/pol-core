extern alias ApiHost;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenIddict.Server.AspNetCore;

namespace Hosts.Tests;

internal sealed class TokenFlowFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", Integration.Tests.IntegrationDb.AppConn);
        builder.UseSetting("ConnectionStrings:Admin", Integration.Tests.IntegrationDb.AppConn);
        builder.UseSetting("OAuth:Issuer", "https://oauth.task2.test");
        builder.UseSetting("IdentityAccess:AccessTokenMinutes", "15");
        builder.UseSetting("IdentityAccess:RefreshTokenMinutes", "480");
        // Same workforce origin as IdentityAccessLoginFactory: both hosts share the database and the pol-admin row.
        builder.UseSetting("IdentityAccess:WorkforceWebAppBaseUrl", "https://spa.task2.test");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.IgnoreMachineLocalDevelopmentSettings();
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            });
        });
        builder.ConfigureServices(services => services.PostConfigure<OpenIddictServerAspNetCoreOptions>(
            options => options.DisableTransportSecurityRequirement = true));
    }
}

/// <summary>The employee JWT contract the workforce SPA relies on: authorization code + PKCE login, refresh
/// rotation, merchant context selection on refresh, and revocation through logout and /me/sessions.</summary>
[Trait("Capability", "IdentityAccess")]
[Trait("Category", "Integration")]
public sealed class IdentityAccessTokenFlowTests
{
    [Fact]
    [Trait("Requirement", "REQ-2.6")]
    [Trait("Requirement", "REQ-2.14")]
    public async Task Code_pkce_login_issues_a_bearer_jwt_and_refresh_rotates_the_reference_token()
    {
        var accountId = Guid.CreateVersion7();
        await SeedEmployeeAsync(accountId, "Token flow employee");
        using var factory = new TokenFlowFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var first = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);
        Assert.InRange(first.ExpiresIn, 15 * 60 - 5, 15 * 60);

        using var me = await GetAsync(client, "/api/v1/me", first.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(accountId, meJson.RootElement.GetProperty("accountId").GetGuid());
        Assert.Equal("Employee", meJson.RootElement.GetProperty("accountType").GetString());
        Assert.Equal(JsonValueKind.Null, meJson.RootElement.GetProperty("merchantContext").ValueKind);

        var second = await EmployeeTokenFlow.RefreshAsync(client, first.RefreshToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        using var withNewToken = await GetAsync(client, "/api/v1/me", second.AccessToken);
        Assert.Equal(HttpStatusCode.OK, withNewToken.StatusCode);

        // Reference refresh tokens are single use: presenting the redeemed one again is invalid_grant, and
        // OpenIddict treats the reuse as a leak and revokes the whole login (the fresh tokens included).
        using var replay = await EmployeeTokenFlow.TokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = first.RefreshToken,
            ["client_id"] = EmployeeTokenFlow.ClientId,
        });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("invalid_grant", await replay.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var afterReplay = await GetAsync(client, "/api/v1/me", second.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.12")]
    public async Task Refresh_selects_a_merchant_context_only_for_active_access()
    {
        var accountId = Guid.CreateVersion7();
        var merchantId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        await SeedEmployeeAsync(accountId, "Merchant context employee");
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(Integration.Tests.IntegrationDb.SaConn))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantId, $"tok-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 1, 1, 1);
                """, ("@access", accessId), ("@account", accountId), ("@merchant", merchantId));
        }
        using var factory = new TokenFlowFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);

        using var denied = await EmployeeTokenFlow.TokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = login.RefreshToken,
            ["client_id"] = EmployeeTokenFlow.ClientId,
            ["merchant_id"] = Guid.CreateVersion7().ToString("D"),
        });
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains("invalid_grant", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A rejected refresh must not have consumed the refresh token.
        var scoped = await EmployeeTokenFlow.RefreshAsync(client, login.RefreshToken, merchantId);
        using var me = await GetAsync(client, "/api/v1/me", scoped.AccessToken);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(merchantId, meJson.RootElement.GetProperty("merchantContext").GetGuid());

        // The context sticks across a plain refresh and is dropped with an empty merchant_id.
        var kept = await EmployeeTokenFlow.RefreshAsync(client, scoped.RefreshToken);
        using var keptMe = await GetAsync(client, "/api/v1/me", kept.AccessToken);
        using var keptJson = JsonDocument.Parse(await keptMe.Content.ReadAsStringAsync());
        Assert.Equal(merchantId, keptJson.RootElement.GetProperty("merchantContext").GetGuid());
    }

    [Fact]
    [Trait("Requirement", "REQ-2.14")]
    public async Task Logout_and_session_revocation_reject_the_login_tokens_immediately()
    {
        var accountId = Guid.CreateVersion7();
        await SeedEmployeeAsync(accountId, "Logout employee");
        using var factory = new TokenFlowFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var loginA = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);
        var loginB = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);

        using var sessions = await GetAsync(client, "/api/v1/me/sessions", loginB.AccessToken);
        Assert.Equal(HttpStatusCode.OK, sessions.StatusCode);
        var sessionsBody = await sessions.Content.ReadAsStringAsync();
        using var sessionsJson = JsonDocument.Parse(sessionsBody);
        var live = sessionsJson.RootElement.EnumerateArray()
            .Where(x => x.GetProperty("live").GetBoolean())
            .Select(x => x.GetProperty("sessionId").GetString()!)
            .ToArray();
        Assert.True(live.Length >= 2, $"expected two live sessions, got {live.Length}");
        Assert.DoesNotContain("token", sessionsBody, StringComparison.OrdinalIgnoreCase);

        // The list is newest first: revoking the oldest login (A) from login B leaves B untouched.
        using var revoke = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/me/sessions/{live[^1]}");
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginB.AccessToken);
        revoke.Headers.Add("Idempotency-Key", $"revoke-{Guid.NewGuid():N}");
        using var revoked = await client.SendAsync(revoke);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using var revokedA = await GetAsync(client, "/api/v1/me", loginA.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, revokedA.StatusCode);
        using var stillB = await GetAsync(client, "/api/v1/me", loginB.AccessToken);
        Assert.Equal(HttpStatusCode.OK, stillB.StatusCode);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loginB.AccessToken);
        using var loggedOut = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        foreach (var token in new[] { loginA.AccessToken, loginB.AccessToken })
        {
            using var rejected = await GetAsync(client, "/api/v1/me", token);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }
        foreach (var refresh in new[] { loginA.RefreshToken, loginB.RefreshToken })
        {
            using var rejected = await EmployeeTokenFlow.TokenAsync(client, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = EmployeeTokenFlow.ClientId,
            });
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        // Logout needs a Bearer identity: a cookie-less anonymous call is not a logged-out success.
        using var anonymous = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.10")]
    public async Task Authorization_version_bump_rejects_issued_tokens_until_refresh_resyncs()
    {
        var accountId = Guid.CreateVersion7();
        await SeedEmployeeAsync(accountId, "Version employee");
        using var factory = new TokenFlowFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await EmployeeTokenFlow.LoginAsync(client, factory.Services, accountId);

        await using (var bump = await Integration.Tests.IntegrationDb.OpenAsync(Integration.Tests.IntegrationDb.SaConn))
            await Integration.Tests.IntegrationDb.ExecAsync(bump,
                "UPDATE acct.Accounts SET AuthorizationVersion = AuthorizationVersion + 1 WHERE Id=@account;",
                ("@account", accountId));

        using var stale = await GetAsync(client, "/api/v1/me", login.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        var resynced = await EmployeeTokenFlow.RefreshAsync(client, login.RefreshToken);
        using var fresh = await GetAsync(client, "/api/v1/me", resynced.AccessToken);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.6")]
    public async Task Authorize_without_a_login_cookie_challenges_entra_and_returns_to_the_same_request()
    {
        using var factory = new TokenFlowFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await EmployeeTokenFlow.EnsureClientAsync(factory.Services);

        // The workforce provider is not configured on this host: the challenge cannot start, and the answer
        // says so instead of issuing a code to an anonymous caller.
        using var response = await client.GetAsync(EmployeeTokenFlow.AuthorizeUrl(
            "challenge", EmployeeTokenFlow.RedirectUriFor(factory.Services)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("capability_not_configured", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private static async Task SeedEmployeeAsync(Guid accountId, string displayName)
    {
        await using var seed = await Integration.Tests.IntegrationDb.OpenAsync(Integration.Tests.IntegrationDb.SaConn);
        await Integration.Tests.IntegrationDb.ExecAsync(seed, """
            INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
            VALUES (@account, 1, @name, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            """, ("@account", accountId), ("@name", displayName));
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request);
    }
}
