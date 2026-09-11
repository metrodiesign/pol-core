extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Accounts.Application;
using Admins.Application;
using Admins.Application.Users;
using Iam.Domain.Permissions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

sealed class Task8A1AdminAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Task8A1Admin";
    public const string Header = "X-Task8-Admin";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.ContainsKey(Header))
            return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("sub", Task8A1SqlFactory.AdminId.ToString("D"))], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

sealed class Task8A1AdminScope : IAdminScope
{
    public bool IsBound => true;
    public Resolution Current { get; } = new(
        Task8A1SqlFactory.AdminId,
        "task8-a1@example.test",
        Admins.Domain.Users.Tier.Super,
        AccessibleMerchants.All)
    {
        Permissions = Keys.AllKeys,
        AuthorizationVersion = 0,
    };
    public AccessibleMerchants Accessible => Current.Accessible;
}

class Task8A1SqlFactory : WebApplicationFactory<ApiHost::Program>
{
    public static readonly Guid AdminId = Guid.Parse("a8100000-0000-4000-8000-000000000001");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", Task2AppConnection());
        builder.UseSetting("ConnectionStrings:Admin", Task2AppConnection());
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
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, Task8A1AdminAuthHandler>(Task8A1AdminAuthHandler.SchemeName, _ => { });
            services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(options =>
                options.AddPolicy("admin", policy => policy
                    .AddAuthenticationSchemes(Task8A1AdminAuthHandler.SchemeName)
                    .RequireAuthenticatedUser()));
            services.RemoveAll<IAdminScope>();
            services.AddScoped<IAdminScope, Task8A1AdminScope>();
            foreach (var descriptor in services
                .Where(x => x.ServiceType == typeof(IHostedService)
                    && x.ImplementationType?.Name is "NotificationDeliveryDispatcher" or "WebhookDeliveryDispatcher")
                .ToArray())
                services.Remove(descriptor);
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

[Trait("Capability", "ApiOperations")]
[Collection("ApiOperationsSql")]
[Trait("Category", "Integration")]
public sealed class Task8IdentityAccessA1SqlTests
{
    [Fact]
    [Trait("Requirement", "REQ-2")]
    [Trait("Requirement", "REQ-10")]
    public async Task System_client_create_patch_access_key_revoke_flow_uses_real_sql_and_guards()
    {
        using var factory = new Task8A1SqlFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var merchantId = Guid.CreateVersion7();
        var clientId = $"task8-a1-{Guid.NewGuid():N}";
        var runTag = Guid.NewGuid().ToString("N");
        var systemClientId = Guid.Empty;
        var accountId = Guid.Empty;

        await using (var connection = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.AppConn))
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(connection, merchantId, $"vcommerce-{Guid.NewGuid():N}"[..20]);

        try
        {
            var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system-clients")
            {
                Content = JsonContent.Create(new
                {
                    merchantId,
                    clientId,
                    displayName = "Task8 A1 SYSTEM",
                    environment = "SANDBOX",
                    scopes = new[] { "payment.view" },
                }),
            };
            AddAdminHeaders(create, $"create-{runTag}");
            using var created = await client.SendAsync(create);
            var createdBody = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
            var createdJson = JsonNode.Parse(createdBody)!.AsObject();
            systemClientId = Guid.Parse(createdJson["systemClientId"]!.ToString());
            accountId = Guid.Parse(createdJson["accountId"]!.ToString());
            var firstEtag = created.Headers.ETag!.Tag;

            var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system-clients")
            {
                Content = JsonContent.Create(new
                {
                    merchantId,
                    clientId,
                    displayName = "Task8 A1 SYSTEM",
                    environment = "SANDBOX",
                    scopes = new[] { "payment.view" },
                }),
            };
            AddAdminHeaders(replay, $"create-{runTag}");
            using var replayed = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
            Assert.Equal(systemClientId,
                Guid.Parse(JsonNode.Parse(await replayed.Content.ReadAsStringAsync())!["systemClientId"]!.ToString()));

            var reused = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system-clients")
            {
                Content = JsonContent.Create(new
                {
                    merchantId,
                    clientId,
                    displayName = "Task8 A1 SYSTEM changed",
                    environment = "SANDBOX",
                    scopes = new[] { "payment.view" },
                }),
            };
            AddAdminHeaders(reused, $"create-{runTag}");
            using var reusedResponse = await client.SendAsync(reused);
            Assert.Equal(HttpStatusCode.Conflict, reusedResponse.StatusCode);

            await using (var auditConnection = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConn))
            {
                var operationCount = await Integration.Tests.IntegrationDb.ScalarAsync(auditConnection,
                    "SELECT COUNT_BIG(*) FROM admin.OperationRecords WHERE ActorId=@actor AND Operation=@operation AND IdempotencyKey=@key;",
                    ("@actor", Task8A1SqlFactory.AdminId), ("@operation", "system-client.create"), ("@key", $"create-{runTag}"));
                Assert.Equal(1L, Convert.ToInt64(operationCount));
            }

            using var read = await SendAsync(client, HttpMethod.Get, $"/api/v1/system-clients/{systemClientId}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/system-clients/{systemClientId}")
            {
                Content = JsonContent.Create(new { displayName = "Task8 A1 SYSTEM renamed", status = "Active" }),
            };
            AddAdminHeaders(patch, $"patch-{runTag}", firstEtag);
            using var patched = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            var secondEtag = patched.Headers.ETag!.Tag;

            using var identityScope = factory.Services.CreateScope();
            var httpAccessor = identityScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            httpAccessor.HttpContext = new DefaultHttpContext();
            var identities = identityScope.ServiceProvider.GetRequiredService<IIdentityAccessQuery>();
            var sessionManager = identityScope.ServiceProvider.GetRequiredService<ApiIdentity.BffSessionManager>();
            var account = await identities.FindAccountAsync(accountId, default);
            Assert.NotNull(account);
            var session = await sessionManager.CreateAsync(account!, null, null, null, null, "/", default);
            httpAccessor.HttpContext = null;
            using (var sessionList = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/sessions"))
            {
                sessionList.Headers.Add("Cookie", $"{ApiIdentity.BffSessionManager.SessionCookieNameDevHttp}={session.SessionToken}");
                using var listed = await client.SendAsync(sessionList);
                Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
                Assert.Contains(session.Ticket.Id.ToString("D"), await listed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            }

            using (var sessionRevoke = new HttpRequestMessage(HttpMethod.Delete,
                       $"/api/v1/me/sessions/{session.Ticket.Id:D}"))
            {
                sessionRevoke.Headers.Add("Cookie",
                    $"{ApiIdentity.BffSessionManager.SessionCookieNameDevHttp}={session.SessionToken}; "
                    + $"{ApiIdentity.BffSessionManager.CsrfCookieName}={session.CsrfToken}");
                sessionRevoke.Headers.Add(ApiIdentity.BffSessionManager.HeaderName, session.CsrfToken);
                sessionRevoke.Headers.Add("Idempotency-Key", $"session-revoke-{runTag}");
                using var revokedSession = await client.SendAsync(sessionRevoke);
                Assert.Equal(HttpStatusCode.NoContent, revokedSession.StatusCode);
            }

            var key = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/system-clients/{systemClientId}/keys")
            {
                Content = JsonContent.Create(new
                {
                    jwk = new { kty = "RSA", n = "AQ", e = "AQAB" },
                    applicationId = clientId,
                    kid = "task8-key-1",
                    algorithm = "RS256",
                    validFrom = DateTime.UtcNow.AddMinutes(-1),
                    validUntil = DateTime.UtcNow.AddHours(1),
                    auditReference = "task8-a1",
                }),
            };
            AddAdminHeaders(key, $"key-{runTag}");
            using var keyCreated = await client.SendAsync(key);
            var keyBody = JsonNode.Parse(await keyCreated.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(HttpStatusCode.Created, keyCreated.StatusCode);
            var keyId = Guid.Parse(keyBody["keyId"]!.ToString());

            using var keys = await SendAsync(client, HttpMethod.Get, $"/api/v1/system-clients/{systemClientId}/keys");
            Assert.Equal(HttpStatusCode.OK, keys.StatusCode);
            Assert.Contains("task8-key-1", await keys.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var access = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/system-clients/{systemClientId}/access")
            {
                Content = JsonContent.Create(new { scopes = new[] { "payment.view", "payment.create" } }),
            };
            AddAdminHeaders(access, $"access-{runTag}", secondEtag);
            using var replaced = await client.SendAsync(access);
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);

            var revoke = new HttpRequestMessage(HttpMethod.Delete,
                $"/api/v1/system-clients/{systemClientId}/keys/{keyId}");
            AddAdminHeaders(revoke, $"key-revoke-{runTag}");
            using var revoked = await client.SendAsync(revoke);
            Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

            var stale = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/system-clients/{systemClientId}")
            {
                Content = JsonContent.Create(new { displayName = "stale", status = "Active" }),
            };
            AddAdminHeaders(stale, $"patch-stale-{runTag}", firstEtag);
            using var staleResponse = await client.SendAsync(stale);
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        }
        finally
        {
            await using var connection = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConn);
            await Integration.Tests.IntegrationDb.ExecAsync(connection,
                "DELETE FROM acct.ClientKeyPolicies WHERE SystemClientId=@client; DELETE FROM access.SystemClientScopes WHERE SystemClientId=@client; DELETE FROM acct.SystemClients WHERE Id=@client; DELETE FROM acct.Accounts WHERE Id=@account; DELETE FROM merch.Merchants WHERE Id=@merchant;",
                ("@client", systemClientId), ("@account", accountId), ("@merchant", merchantId));
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        AddAdminHeaders(request, "read-1");
        return await client.SendAsync(request);
    }

    private static void AddAdminHeaders(HttpRequestMessage request, string idempotency, string? etag = null)
    {
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, "csrf-task8");
        var csrfCookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{csrfCookieName}=csrf-task8");
        request.Headers.Add("Idempotency-Key", idempotency);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }
}
