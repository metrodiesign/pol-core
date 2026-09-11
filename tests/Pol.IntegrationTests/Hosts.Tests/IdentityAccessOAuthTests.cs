extern alias ApiHost;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

namespace Hosts.Tests;

file sealed class IdentityAccessOAuthFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", Task2AppConnection());
        builder.UseSetting("ConnectionStrings:Admin", "Server=(local);Database=pol_test;Trusted_Connection=True;");
        builder.UseSetting("OAuth:Issuer", "https://oauth.task2.test");
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
public sealed class IdentityAccessOAuthTests
{
    [Fact]
    [Trait("Requirement", "REQ-2.7")]
    [Trait("Requirement", "REQ-2.8")]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-10.6")]
    public async Task Token_endpoint_requires_private_key_jwt_for_system_client_credentials()
    {
        using var factory = new IdentityAccessOAuthFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "unknown-system-client",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = "malformed-assertion",
            })
        };

        var response = await client.SendAsync(request);
        // OAuth client authentication failures are 401 (RFC 6749 §5.2); malformed request syntax remains 400.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid_client", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Basic", string.Join(";", response.Headers.WwwAuthenticate), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Requirement", "REQ-2.7")]
    [Trait("Requirement", "REQ-2.9")]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-10.6")]
    public async Task Registered_jwk_private_key_jwt_issues_a_system_token_without_refresh_token()
    {
        using var factory = new IdentityAccessOAuthFactory();
        var clientId = $"system-{Guid.NewGuid():N}";
        var applicationId = $"app-{Guid.NewGuid():N}";
        var keyId = "task2-jwk";
        var accountId = Guid.NewGuid();
        var systemClientId = Guid.NewGuid();
        var merchantId = Guid.NewGuid();
        using var rsa = RSA.Create(2048);
        var jwkSet = CreateJwkSet(rsa, keyId);
        var now = DateTime.UtcNow;

        await using (var connection = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.AppConn))
        {
            await Integration.Tests.IntegrationDb.ExecAsync(connection,
                """
                INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 3, N'Task2 System', 1, 0, @now, @now);
                INSERT acct.SystemClients (Id, AccountId, ClientId, MerchantId, Environment, Status, AllowedGrantTypes, CreatedAt, UpdatedAt)
                VALUES (@systemClient, @account, @client, @merchant, N'SANDBOX', 1, N'client_credentials', @now, @now);
                INSERT acct.ClientKeyPolicies (Id, SystemClientId, ApplicationId, KeyId, Algorithm, ValidFrom, ValidUntil, Status)
                VALUES (@key, @systemClient, @client, @kid, N'PS256', DATEADD(MINUTE, -1, @now), DATEADD(HOUR, 1, @now), 1);
                INSERT oauth.OpenIddictApplications
                    (Id, ApplicationType, ClientId, ClientType, ConsentType, DisplayName, JsonWebKeySet, Permissions)
                VALUES (@application, N'web', @client, N'confidential', N'implicit', N'Task2 System', @jwk, @permissions);
                """,
                ("@account", accountId),
                ("@systemClient", systemClientId),
                ("@client", clientId),
                ("@merchant", merchantId),
                ("@key", Guid.NewGuid()),
                ("@kid", keyId),
                ("@now", now),
                ("@application", applicationId),
                ("@jwk", jwkSet),
                ("@permissions", $"[\"{OpenIddictConstants.Permissions.Endpoints.Token}\",\"{OpenIddictConstants.Permissions.GrantTypes.ClientCredentials}\"]"));
        }

        try
        {
            using var httpClient = factory.CreateClient();
            var assertion = CreateAssertion(rsa, clientId, keyId, now);
            using var request = CreateTokenRequest(clientId, assertion);

            using var response = await httpClient.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            Assert.True(document.RootElement.TryGetProperty("access_token", out var accessToken));
            Assert.False(string.IsNullOrWhiteSpace(accessToken.GetString()));
            Assert.False(document.RootElement.TryGetProperty("refresh_token", out _));

            await SetStatusAsync(accountId, systemClientId, accountStatus: 2, clientStatus: 1);
            using var accountDisabledResponse = await httpClient.SendAsync(
                CreateTokenRequest(clientId, CreateAssertion(rsa, clientId, keyId, now.AddSeconds(1))));
            Assert.Equal(HttpStatusCode.Unauthorized, accountDisabledResponse.StatusCode);

            await SetStatusAsync(accountId, systemClientId, accountStatus: 1, clientStatus: 2);
            using var clientDisabledResponse = await httpClient.SendAsync(
                CreateTokenRequest(clientId, CreateAssertion(rsa, clientId, keyId, now.AddSeconds(2))));
            Assert.Equal(HttpStatusCode.Unauthorized, clientDisabledResponse.StatusCode);
        }
        finally
        {
            await using var connection = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.AppConn);
            await Integration.Tests.IntegrationDb.ExecAsync(connection,
                "DELETE FROM oauth.OpenIddictTokens WHERE ApplicationId=@application; DELETE FROM oauth.OpenIddictApplications WHERE Id=@application; DELETE FROM acct.ClientKeyPolicies WHERE SystemClientId=@systemClient; DELETE FROM acct.SystemClients WHERE Id=@systemClient; DELETE FROM acct.Accounts WHERE Id=@account;",
                ("@application", applicationId),
                ("@systemClient", systemClientId),
                ("@account", accountId));
        }
    }

    private static HttpRequestMessage CreateTokenRequest(string clientId, string assertion) =>
        new(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = OpenIddictConstants.GrantTypes.ClientCredentials,
                ["client_id"] = clientId,
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = assertion,
            })
        };

    private static async Task SetStatusAsync(Guid accountId, Guid systemClientId, int accountStatus, int clientStatus)
    {
        await using var connection = await Integration.Tests.IntegrationDb.OpenAsync(
            Integration.Tests.IntegrationDb.AppConn);
        await Integration.Tests.IntegrationDb.ExecAsync(connection,
            "UPDATE acct.Accounts SET Status=@accountStatus WHERE Id=@account; UPDATE acct.SystemClients SET Status=@clientStatus WHERE Id=@systemClient;",
            ("@accountStatus", accountStatus),
            ("@clientStatus", clientStatus),
            ("@account", accountId),
            ("@systemClient", systemClientId));
    }

    [Fact]
    [Trait("Requirement", "REQ-2.14")]
    public async Task OpenIddict_refresh_token_rotator_redeems_old_reference_and_creates_one_successor()
    {
        using var factory = new IdentityAccessOAuthFactory();
        using var scope = factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var rotator = scope.ServiceProvider.GetRequiredService<
            ApiHost::Api.IdentityAccess.OpenIddictRefreshTokenRotator>();
        var raw = $"refresh-{Guid.NewGuid():N}";
        var descriptor = new OpenIddictTokenDescriptor
        {
            ReferenceId = raw,
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = Guid.NewGuid().ToString("D"),
            Type = "refresh_token",
            CreationDate = DateTimeOffset.UtcNow,
            ExpirationDate = DateTimeOffset.UtcNow.AddMinutes(10),
        };
        await manager.CreateAsync(descriptor, default);

        var successor = await rotator.RotateAsync(raw, default);

        Assert.False(string.IsNullOrWhiteSpace(successor));
        Assert.NotEqual(raw, successor);
        var oldToken = await manager.FindByReferenceIdAsync(raw, default);
        var newToken = await manager.FindByReferenceIdAsync(successor!, default);
        Assert.NotNull(oldToken);
        Assert.NotNull(newToken);
        Assert.Equal(OpenIddictConstants.Statuses.Redeemed,
            await manager.GetStatusAsync(oldToken!, default));
        Assert.Equal(OpenIddictConstants.Statuses.Valid,
            await manager.GetStatusAsync(newToken!, default));
    }

    [Fact]
    [Trait("Requirement", "REQ-2.14")]
    public async Task Selected_logout_revokes_the_openiddict_refresh_token_and_bff_session()
    {
        using var factory = new IdentityAccessOAuthFactory();
        using var scope = factory.Services.CreateScope();
        var tokenManager = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var rawRefreshToken = $"logout-refresh-{Guid.NewGuid():N}";
        var token = await tokenManager.CreateAsync(new OpenIddictTokenDescriptor
        {
            ReferenceId = rawRefreshToken,
            Status = OpenIddictConstants.Statuses.Valid,
            Subject = Guid.NewGuid().ToString("D"),
            Type = "refresh_token",
            CreationDate = DateTimeOffset.UtcNow,
            ExpirationDate = DateTimeOffset.UtcNow.AddMinutes(10),
        }, default);
        var store = new InMemoryBffStore();
        var account = Account.Create(AccountType.Employee, "Logout Employee", DateTime.UtcNow);
        var manager = new ApiHost::Api.IdentityAccess.BffSessionManager(
            store,
            new EphemeralDataProtectionProvider(),
            new FixedClock(),
            Options.Create(new ApiHost::Api.IdentityAccess.IdentityAccessOptions { BffSessionMinutes = 60 }));
        var issue = await manager.CreateAsync(account, null, "access", rawRefreshToken, null, "/", default);
        var http = new DefaultHttpContext();
        var cookieName = ApiHost::Api.IdentityAccess.BffSessionManager.SessionCookieNameDevHttp;
        http.Request.Headers.Cookie = $"{cookieName}={issue.SessionToken}";

        var result = await ApiHost::Api.IdentityAccess.IdentityAccessEndpoints.Logout(
            http, manager, store, default, tokenManager);

        Assert.Equal(StatusCodes.Status204NoContent,
            Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(issue.Ticket.IsLiveAt(DateTime.UtcNow, account.AuthorizationVersion));
        Assert.Equal(OpenIddictConstants.Statuses.Revoked,
            await tokenManager.GetStatusAsync(token, default));
    }

    private sealed class FixedClock : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    private sealed class InMemoryBffStore : IBffSessionStore
    {
        private readonly List<BffSessionTicket> _tickets = [];

        public Task<BffSessionTicket?> FindByHashAsync(byte[] ticketKeyHash, CancellationToken cancellationToken) =>
            Task.FromResult(_tickets.FirstOrDefault(ticket => ticket.TicketKeyHash.SequenceEqual(ticketKeyHash)));

        public void Add(BffSessionTicket ticket) => _tickets.Add(ticket);

        public Task RevokeAsync(BffSessionTicket ticket, DateTime now, CancellationToken cancellationToken)
        {
            ticket.Revoke(now);
            return Task.CompletedTask;
        }

        public Task ReplaceAsync(BffSessionTicket current, BffSessionTicket replacement, DateTime now,
            CancellationToken cancellationToken)
        {
            current.Revoke(now);
            _tickets.Add(replacement);
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static string CreateJwkSet(RSA rsa, string keyId)
    {
        var parameters = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    n = Base64UrlEncoder.Encode(parameters.Modulus!),
                    e = Base64UrlEncoder.Encode(parameters.Exponent!),
                    kid = keyId,
                    use = "sig",
                    alg = "PS256"
                }
            }
        });
    }

    private static string CreateAssertion(RSA rsa, string clientId, string keyId, DateTime now)
    {
        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: clientId,
            audience: "https://oauth.task2.test",
            subject: new ClaimsIdentity([new Claim("sub", clientId), new Claim("jti", $"jti-{Guid.NewGuid():N}")]),
            notBefore: now.AddSeconds(-1),
            expires: now.AddSeconds(30),
            issuedAt: now,
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(rsa) { KeyId = keyId },
                SecurityAlgorithms.RsaSsaPssSha256));
        token.Header["typ"] = "client-authentication+jwt";
        return handler.WriteToken(token);
    }
}
