extern alias ApiHost;
using ApiIdentity = ApiHost::Api.IdentityAccess;
using ApiAdmin = ApiHost::Api.Admins;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Products.Application.Ports;
using Products.Domain;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hosts.Tests;

file class IdentityAccessOAuthFactory : WebApplicationFactory<ApiHost::Program>
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

file sealed class IdentityAccessOrderFactory(string database, ISpDocumentGateway gateway)
    : IdentityAccessOAuthFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var connection = Integration.Tests.IntegrationDb.AppConnFor(database);
        builder.UseSetting("ConnectionStrings:App", connection);
        builder.UseSetting("ConnectionStrings:Admin", connection);
        builder.UseSetting("ConnectionStrings:Platform", connection);
        builder.UseSetting("OAuth:Issuer", "https://oauth.task2.test");
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<OpenIddictServerAspNetCoreOptions>(
                options => options.DisableTransportSecurityRequirement = true);
            services.RemoveAll<ISpDocumentGateway>();
            services.AddSingleton<ISpDocumentGateway>(gateway);
        });
    }
}

file sealed class IdentityAccessOrderAdminFactory(string database, ISpDocumentGateway gateway)
    : Task8A1SqlFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var connection = Integration.Tests.IntegrationDb.AppConnFor(database);
        builder.UseSetting("ConnectionStrings:App", connection);
        builder.UseSetting("ConnectionStrings:Admin", connection);
        builder.UseSetting("ConnectionStrings:Platform", connection);
        builder.UseSetting("OAuth:Issuer", "https://oauth.task2.test");
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<OpenIddictServerAspNetCoreOptions>(
                options => options.DisableTransportSecurityRequirement = true);
            services.RemoveAll<ISpDocumentGateway>();
            services.AddSingleton<ISpDocumentGateway>(gateway);
        });
    }
}

file sealed class OAuthOrderDocumentGateway(SpDocumentItem document) : ISpDocumentGateway
{
    public SpDocumentItem Document { get; set; } = document;
    public bool ReturnBothRoutes { get; set; }

    public Task<SpDocumentSearchResult> SearchAsync(
        SpDocumentSearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SpDocumentSearchResult(
            new SpPaginationMetadata(1, 1, 1, 25, false, false, "EXACT", 6), [Document]));

    public Task<SpDocumentItem?> LookupAsync(
        SpDocumentLookupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SpDocumentItem?>(
            request.ProductGroup == ProductGroup.CMI || ReturnBothRoutes ? Document : null);
}

[Trait("Capability", "IdentityAccess")]
[Trait("Category", "Integration")]
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
                ("@permissions", $"[\"{OpenIddictConstants.Permissions.Endpoints.Token}\",\"{OpenIddictConstants.Permissions.GrantTypes.ClientCredentials}\",\"{OpenIddictConstants.Permissions.Prefixes.Scope}order.write\"]"));
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

    [Fact]
    [Trait("Requirement", "REQ-2.7")]
    [Trait("Requirement", "REQ-6.7")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task System_client_management_provisions_scope_key_and_canonical_order_uses_production_pricing()
    {
        var database = $"PolPr253OAuthOrder{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var branchId = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();
        var clientId = $"system-order-{Guid.NewGuid():N}";
        var runTag = Guid.NewGuid().ToString("N");
        using var rsa = RSA.Create(2048);
        var keyId = "task-pr253-order-jwk";
        var gateway = new OAuthOrderDocumentGateway(CreateOrderDocument("DOC-OAUTH", "SALE-OAUTH"));
        await using (var connection = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                connection, merchantId, $"oauth-order-{Guid.NewGuid():N}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(connection, """
                INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@branch, @merchant, N'branch-oauth', N'OAuth branch', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@sale, @merchant, @branch, N'SALE-OAUTH', N'OAuth sale', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                """,
                ("@branch", branchId), ("@merchant", merchantId), ("@sale", saleId));
        }

        try
        {
            using var factory = new IdentityAccessOrderAdminFactory(database, gateway);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/system-clients")
            {
                Content = JsonContent.Create(new
                {
                    merchantId,
                    clientId,
                    displayName = "OAuth Order System",
                    environment = "SANDBOX",
                    scopes = new[] { "order.read", "order.write", "checkout.write" },
                }),
            };
            AddOrderAdminHeaders(create, $"create-{runTag}");
            using var created = await client.SendAsync(create);
            var createdBody = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
            var createdJson = JsonNode.Parse(createdBody)!.AsObject();
            var systemClientId = Guid.Parse(createdJson["systemClientId"]!.ToString());
            var accountId = Guid.Parse(createdJson["accountId"]!.ToString());

            var access = new HttpRequestMessage(
                HttpMethod.Put, $"/api/v1/accounts/{accountId:D}/merchant-access/{merchantId:D}")
            {
                Content = JsonContent.Create(new
                {
                    dataScope = "Merchant",
                    roleIds = Array.Empty<Guid>(),
                    branchIds = Array.Empty<Guid>(),
                    paymentMethods = Array.Empty<string>(),
                }),
            };
            AddOrderAdminHeaders(access, $"access-{runTag}", "\"v0\"");
            using var accessResponse = await client.SendAsync(access);
            Assert.True(accessResponse.StatusCode == HttpStatusCode.OK,
                await accessResponse.Content.ReadAsStringAsync());

            var jwk = JsonNode.Parse(CreateJwkSet(rsa, keyId))!.AsObject()["keys"]!.AsArray()[0]!.AsObject();
            var key = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/system-clients/{systemClientId:D}/keys")
            {
                Content = JsonContent.Create(new
                {
                    jwk,
                    applicationId = clientId,
                    kid = keyId,
                    algorithm = "PS256",
                    validFrom = DateTime.UtcNow.AddMinutes(-1),
                    validUntil = DateTime.UtcNow.AddHours(1),
                    auditReference = "pr253-order",
                }),
            };
            AddOrderAdminHeaders(key, $"key-{runTag}");
            using var keyResponse = await client.SendAsync(key);
            Assert.True(keyResponse.StatusCode == HttpStatusCode.Created,
                await keyResponse.Content.ReadAsStringAsync());

            using var tokenResponse = await client.SendAsync(
                CreateTokenRequest(clientId, CreateAssertion(rsa, clientId, keyId, DateTime.UtcNow),
                    "order.read order.write checkout.write"));
            var tokenBody = await tokenResponse.Content.ReadAsStringAsync();
            Assert.True(tokenResponse.StatusCode == HttpStatusCode.OK, tokenBody);
            var accessToken = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString();
            Assert.False(string.IsNullOrWhiteSpace(accessToken));

            using var order = CanonicalOrderRequest(
                merchantId, saleId, "oauth-order-1", accessToken!, "125.0000", "125.0000");
            using var orderResponse = await client.SendAsync(order);
            var orderBody = await orderResponse.Content.ReadAsStringAsync();
            Assert.True(orderResponse.StatusCode == HttpStatusCode.Created, orderBody);
            using var orderJson = JsonDocument.Parse(orderBody);
            var orderId = orderJson.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
            var draftVersion = orderJson.RootElement.GetProperty("order").GetProperty("version").GetInt64();

            using var get = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}");
            get.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var getResponse = await client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

            using var items = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}/items?merchantId={merchantId:D}");
            items.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var itemsResponse = await client.SendAsync(items);
            Assert.Equal(HttpStatusCode.OK, itemsResponse.StatusCode);

            using var history = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}/history?merchantId={merchantId:D}");
            history.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var historyResponse = await client.SendAsync(history);
            Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);

            using var links = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}/payment-links?merchantId={merchantId:D}");
            links.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var linksResponse = await client.SendAsync(links);
            Assert.Equal(HttpStatusCode.OK, linksResponse.StatusCode);

            using var patch = new HttpRequestMessage(
                HttpMethod.Patch, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "insurance",
                    items = new[] { new { productReference = "DOC-OAUTH", quantity = 1 } },
                    ownerSaleId = saleId,
                    ownerBranchId = (Guid?)null,
                }),
            };
            patch.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            patch.Headers.Add("If-Match", $"\"v{draftVersion}\"");
            using var patchResponse = await client.SendAsync(patch);
            var patchBody = await patchResponse.Content.ReadAsStringAsync();
            Assert.True(patchResponse.StatusCode == HttpStatusCode.OK, patchBody);
            using var patchJson = JsonDocument.Parse(patchBody);
            var patchedVersion = patchJson.RootElement.GetProperty("version").GetInt64();

            using var issue = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/issue?merchantId={merchantId:D}");
            issue.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            issue.Headers.Add("If-Match", $"\"v{patchedVersion}\"");
            issue.Headers.Add("Idempotency-Key", "oauth-order-issue");
            using var issueResponse = await client.SendAsync(issue);
            var issueBody = await issueResponse.Content.ReadAsStringAsync();
            Assert.True(issueResponse.StatusCode == HttpStatusCode.OK, issueBody);
            using var issueJson = JsonDocument.Parse(issueBody);
            var issuedVersion = issueJson.RootElement.GetProperty("order").GetProperty("version").GetInt64();
            var issuedLinkId = issueJson.RootElement.GetProperty("paymentLink").GetProperty("linkId").GetGuid();

            using var legacyResend = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/summary/resend?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { }),
            };
            legacyResend.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            legacyResend.Headers.Add("If-Match", $"\"v{issuedVersion}\"");
            legacyResend.Headers.Add("Idempotency-Key", "oauth-order-legacy-resend");
            using var legacyResendResponse = await client.SendAsync(legacyResend);
            Assert.True(legacyResendResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                await legacyResendResponse.Content.ReadAsStringAsync());
            await using (var legacyVerify = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(issuedVersion, Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(
                    legacyVerify, "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
            }

            using var missingReadTokenResponse = await client.SendAsync(CreateTokenRequest(
                clientId, CreateAssertion(rsa, clientId, keyId, DateTime.UtcNow.AddSeconds(1)),
                "order.write checkout.write"));
            var missingReadTokenBody = await missingReadTokenResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, missingReadTokenResponse.StatusCode);
            var missingReadToken = JsonDocument.Parse(missingReadTokenBody)
                .RootElement.GetProperty("access_token").GetString();
            using var missingRead = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}");
            missingRead.Headers.Authorization = new AuthenticationHeaderValue("Bearer", missingReadToken);
            using var missingReadResponse = await client.SendAsync(missingRead);
            Assert.Equal(HttpStatusCode.Forbidden, missingReadResponse.StatusCode);
            using var missingReadList = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders?merchantId={merchantId:D}");
            missingReadList.Headers.Authorization = new AuthenticationHeaderValue("Bearer", missingReadToken);
            using var missingReadListResponse = await client.SendAsync(missingReadList);
            Assert.Equal(HttpStatusCode.Forbidden, missingReadListResponse.StatusCode);

            using var missingCheckoutTokenResponse = await client.SendAsync(CreateTokenRequest(
                clientId, CreateAssertion(rsa, clientId, keyId, DateTime.UtcNow.AddSeconds(2)),
                "order.read order.write"));
            var missingCheckoutTokenBody = await missingCheckoutTokenResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, missingCheckoutTokenResponse.StatusCode);
            var missingCheckoutToken = JsonDocument.Parse(missingCheckoutTokenBody)
                .RootElement.GetProperty("access_token").GetString();
            using var missingCheckoutRotate = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/payment-links?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { sendNotification = false }),
            };
            missingCheckoutRotate.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", missingCheckoutToken);
            missingCheckoutRotate.Headers.Add("If-Match", $"\"v{issuedVersion}\"");
            missingCheckoutRotate.Headers.Add("Idempotency-Key", "oauth-order-missing-checkout");
            using var missingCheckoutResponse = await client.SendAsync(missingCheckoutRotate);
            Assert.Equal(HttpStatusCode.Forbidden, missingCheckoutResponse.StatusCode);

            using var mixedBff = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}");
            mixedBff.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            mixedBff.Headers.Add("Cookie",
                $"{ApiIdentity.BffSessionManager.SessionCookieNameDevHttp}=mixed");
            using var mixedBffResponse = await client.SendAsync(mixedBff);
            Assert.Equal(HttpStatusCode.BadRequest, mixedBffResponse.StatusCode);
            Assert.Contains("ambiguous_authentication_context",
                await mixedBffResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var mixedConsole = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}");
            mixedConsole.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            mixedConsole.Headers.Add("Cookie",
                $"{ApiAdmin.SessionCookies.SessionCookieNameDevHttp}=mixed");
            using var mixedConsoleResponse = await client.SendAsync(mixedConsole);
            Assert.Equal(HttpStatusCode.BadRequest, mixedConsoleResponse.StatusCode);
            Assert.Contains("ambiguous_authentication_context",
                await mixedConsoleResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var rotate = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/payment-links?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { sendNotification = false }),
            };
            rotate.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            rotate.Headers.Add("If-Match", $"\"v{issuedVersion}\"");
            rotate.Headers.Add("Idempotency-Key", "oauth-order-rotate");
            using var rotateResponse = await client.SendAsync(rotate);
            var rotateBody = await rotateResponse.Content.ReadAsStringAsync();
            Assert.True(rotateResponse.StatusCode == HttpStatusCode.Created, rotateBody);
            using var rotateJson = JsonDocument.Parse(rotateBody);
            var rotatedVersion = rotateJson.RootElement.GetProperty("order").GetProperty("version").GetInt64();
            var rotatedLinkId = rotateJson.RootElement.GetProperty("paymentLink").GetProperty("linkId").GetGuid();

            using var revoke = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/payment-links/{rotatedLinkId:D}/revoke")
            {
                Content = JsonContent.Create(new { reason = "phase-a" }),
            };
            revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            revoke.Headers.Add("Idempotency-Key", "oauth-order-revoke");
            using var revokeResponse = await client.SendAsync(revoke);
            Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

            using var cancelCreate = CanonicalOrderRequest(
                merchantId, saleId, $"oauth-order-cancel-{runTag}", accessToken!, "125.0000", "125.0000");
            using var cancelCreateResponse = await client.SendAsync(cancelCreate);
            var cancelCreateBody = await cancelCreateResponse.Content.ReadAsStringAsync();
            Assert.True(cancelCreateResponse.StatusCode == HttpStatusCode.Created, cancelCreateBody);
            using var cancelCreateJson = JsonDocument.Parse(cancelCreateBody);
            var cancelOrderId = cancelCreateJson.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
            var cancelVersion = cancelCreateJson.RootElement.GetProperty("order").GetProperty("version").GetInt64();
            using var cancel = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{cancelOrderId:D}/cancel?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { reason = "phase-a" }),
            };
            cancel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            cancel.Headers.Add("If-Match", $"\"v{cancelVersion}\"");
            cancel.Headers.Add("Idempotency-Key", "oauth-order-cancel");
            using var cancelResponse = await client.SendAsync(cancel);
            var cancelBody = await cancelResponse.Content.ReadAsStringAsync();
            Assert.True(cancelResponse.StatusCode == HttpStatusCode.OK, cancelBody);

            using var forged = CanonicalOrderRequest(
                merchantId, saleId, "oauth-order-forged", accessToken!, "999.0000", "999.0000");
            using var forgedResponse = await client.SendAsync(forged);
            Assert.Equal(HttpStatusCode.Conflict, forgedResponse.StatusCode);
            Assert.Contains("pricing_mismatch", await forgedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var noOwner = CanonicalOrderRequest(
                merchantId, null, "oauth-order-no-owner", accessToken!, "125.0000", "125.0000");
            using var noOwnerResponse = await client.SendAsync(noOwner);
            Assert.Equal(HttpStatusCode.Conflict, noOwnerResponse.StatusCode);
            Assert.Contains("owner_required", await noOwnerResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            gateway.ReturnBothRoutes = true;
            using var ambiguous = CanonicalOrderRequest(
                merchantId, saleId, "oauth-order-ambiguous", accessToken!, "125.0000", "125.0000");
            using var ambiguousResponse = await client.SendAsync(ambiguous);
            Assert.Equal(HttpStatusCode.Conflict, ambiguousResponse.StatusCode);
            Assert.Contains("source_ambiguous", await ambiguousResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            gateway.ReturnBothRoutes = false;
            gateway.Document = gateway.Document with { PaymentStatus = "PAID" };
            using var paid = CanonicalOrderRequest(
                merchantId, saleId, "oauth-order-paid", accessToken!, "125.0000", "125.0000");
            using var paidResponse = await client.SendAsync(paid);
            Assert.Equal(HttpStatusCode.Conflict, paidResponse.StatusCode);
            Assert.Contains("product_unpayable", await paidResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var invalidScope = await client.SendAsync(CreateTokenRequest(
                clientId, CreateAssertion(rsa, clientId, keyId, DateTime.UtcNow.AddSeconds(1)), "payment.create"));
            Assert.Equal(HttpStatusCode.BadRequest, invalidScope.StatusCode);
            Assert.Contains("invalid_scope", await invalidScope.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(2, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static void AddOrderAdminHeaders(HttpRequestMessage request, string key, string? etag = null)
    {
        const string csrf = "pr253-oauth-admin-csrf";
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, csrf);
        var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{cookieName}={csrf}");
        request.Headers.Add("Idempotency-Key", key);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }

    private static HttpRequestMessage CreateTokenRequest(string clientId, string assertion, string? scope = null)
    {
        var values = new Dictionary<string, string>
        {
            ["grant_type"] = OpenIddictConstants.GrantTypes.ClientCredentials,
            ["client_id"] = clientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
        };
        if (scope is not null)
            values["scope"] = scope;
        return new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(values),
        };
    }

    private static HttpRequestMessage CanonicalOrderRequest(
        Guid merchantId, Guid? ownerSaleId, string key, string accessToken,
        string unitPrice, string lineAmount)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
        {
            Content = JsonContent.Create(new
            {
                businessType = "insurance",
                currency = "THB",
                issueNow = false,
                ownerSaleId,
                items = new[]
                {
                    new
                    {
                        productReference = "DOC-OAUTH",
                        productCode = "DOC-OAUTH",
                        productName = "OAuth policy",
                        quantity = 1,
                        unitPrice,
                        discountAmount = "0.0000",
                        taxAmount = "0.0000",
                        lineAmount,
                    },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static SpDocumentItem CreateOrderDocument(string documentNo, string saleCode) => new(
        "Motor", "CMI", "POLICY", documentNo, "2026", "BKK", "REF", "1", "2026", "1",
        "BKK", "AUTO", saleCode, "OAuth sale", null, null, "POL-1", "APP-1", null, null,
        DateTime.UtcNow, DateTime.UtcNow.AddYears(1), "OAuth policy", 100m, 5m, 20m, 125m, 10m,
        10m, DateTime.UtcNow, "1กก1234", "UNPAID");

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
