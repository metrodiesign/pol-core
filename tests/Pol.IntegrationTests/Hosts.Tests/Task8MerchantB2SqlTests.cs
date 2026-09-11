extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Payments.Domain.Capabilities;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hosts.Tests;

[Trait("Capability", "ApiOperations")]
[Collection("ApiOperationsSql")]
public sealed class Task8MerchantB2SqlTests
{
    [Fact]
    [Trait("Requirement", "REQ-5")]
    [Trait("Requirement", "REQ-10")]
    public async Task Provider_account_credential_and_payment_setting_contracts_use_real_sql_and_redaction()
    {
        using var factory = new Task8A1SqlFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var connectionId = Guid.Empty;
        var approvalId = Guid.Empty;
        var secret = $"skey_test_{runTag}";

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConn))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantA, $"b2a-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantB, $"b2b-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                IF NOT EXISTS (SELECT 1 FROM admin.Users WHERE Id=@actor)
                    INSERT admin.Users (Id, Subject, Email, Tier, Status, AuthorizationVersion, CreatedAt)
                    VALUES (@actor, NULL, N'task8-b2@example.test', 2, 1, 0, SYSUTCDATETIME());
                """, ("@actor", Task8A1SqlFactory.AdminId));
        }

        try
        {
            using var providers = await SendAsync(client, HttpMethod.Get, "/api/v1/payment-providers");
            Assert.Equal(HttpStatusCode.OK, providers.StatusCode);
            Assert.Contains(PaymentCapabilityIds.TwoCTwoP.ToString("D"),
                await providers.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            var create = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/merchants/{merchantA:D}/provider-accounts")
            {
                Content = JsonContent.Create(new
                {
                    providerId = PaymentCapabilityIds.TwoCTwoP,
                    displayName = "B2 2C2P",
                    environment = "SANDBOX",
                    configuration = new { accountId = "merchant-account-b2" },
                }),
            };
            AddAdminHeaders(create, $"provider-create-{runTag}");
            using var created = await client.SendAsync(create);
            var createdBody = await created.Content.ReadAsStringAsync();
            Assert.True(created.StatusCode == HttpStatusCode.Created, createdBody);
            Assert.DoesNotContain(secret, createdBody, StringComparison.Ordinal);
            var createdJson = JsonNode.Parse(createdBody)!.AsObject();
            connectionId = Guid.Parse(createdJson["pspConnectionId"]!.ToString());
            Assert.False(createdJson["isEnabled"]!.GetValue<bool>());
            var accountEtag = created.Headers.ETag!.Tag;

            using var crossMerchant = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/merchants/{merchantB:D}/provider-accounts/{connectionId:D}");
            Assert.Equal(HttpStatusCode.NotFound, crossMerchant.StatusCode);

            var credential = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/merchants/{merchantA:D}/provider-accounts/{connectionId:D}/credential-versions")
            {
                Content = JsonContent.Create(new
                {
                    secretFields = new { secretKey = secret },
                    keyId = "b2-key-1",
                    validFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                    validUntil = DateTimeOffset.UtcNow.AddHours(1),
                    pspMerchantId = "b2-merchant",
                }),
            };
            AddAdminHeaders(credential, $"credential-{runTag}", accountEtag);
            using var credentialCreated = await client.SendAsync(credential);
            var credentialBody = await credentialCreated.Content.ReadAsStringAsync();
            Assert.True(credentialCreated.StatusCode == HttpStatusCode.Created, credentialBody);
            Assert.DoesNotContain(secret, credentialBody, StringComparison.Ordinal);
            var credentialJson = JsonNode.Parse(credentialBody)!.AsObject();
            approvalId = Guid.Parse(credentialJson["approvalId"]!.ToString());

            var changedCredential = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/merchants/{merchantA:D}/provider-accounts/{connectionId:D}/credential-versions")
            {
                Content = JsonContent.Create(new
                {
                    secretFields = new { secretKey = $"changed_{secret}" },
                    keyId = "b2-key-1",
                    validFrom = DateTimeOffset.UtcNow.AddMinutes(-1),
                    pspMerchantId = "b2-merchant",
                }),
            };
            AddAdminHeaders(changedCredential, $"credential-{runTag}", accountEtag);
            using var changedCredentialResponse = await client.SendAsync(changedCredential);
            Assert.Equal(HttpStatusCode.Conflict, changedCredentialResponse.StatusCode);

            using var versions = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/merchants/{merchantA:D}/provider-accounts/{connectionId:D}/credential-versions");
            Assert.Equal(HttpStatusCode.OK, versions.StatusCode);
            var versionsBody = await versions.Content.ReadAsStringAsync();
            Assert.DoesNotContain(secret, versionsBody, StringComparison.Ordinal);
            if (!versionsBody.Contains("staged", StringComparison.OrdinalIgnoreCase))
            {
                await using var probe = await Integration.Tests.IntegrationDb.OpenAsync(
                    Integration.Tests.IntegrationDb.SaConn);
                var versionCount = await Integration.Tests.IntegrationDb.ScalarAsync(probe,
                    "SELECT COUNT_BIG(*) FROM merch.VaultSecretVersions WHERE MerchantId=@merchant;",
                    ("@merchant", merchantA));
                var connectionCount = await Integration.Tests.IntegrationDb.ScalarAsync(probe,
                    "SELECT COUNT_BIG(*) FROM txn.PspConnections WHERE Id=@connection AND MerchantId=@merchant;",
                    ("@connection", connectionId), ("@merchant", merchantA));
                var matchingCount = await Integration.Tests.IntegrationDb.ScalarAsync(probe, """
                    SELECT COUNT_BIG(*) FROM txn.PspConnections c
                    JOIN merch.VaultSecretVersions v ON v.MerchantId=c.MerchantId AND v.SecretName=c.SecretRefName
                    WHERE c.Id=@connection AND c.MerchantId=@merchant;
                    """, ("@connection", connectionId), ("@merchant", merchantA));
                Assert.Fail($"Credential version projection was empty: body={versionsBody}; dbVersionCount={versionCount}; connectionCount={connectionCount}; matchingCount={matchingCount}");
            }

            using var settings = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/merchants/{merchantA:D}/payment-settings");
            Assert.Equal(HttpStatusCode.OK, settings.StatusCode);

            var disable = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/merchants/{merchantA:D}/provider-accounts/{connectionId:D}/disable")
            {
                Content = JsonContent.Create(new { reason = "B2 emergency disable" }),
            };
            AddAdminHeaders(disable, $"disable-{runTag}", credentialCreated.Headers.ETag!.Tag);
            using var disabled = await client.SendAsync(disable);
            var disabledBody = await disabled.Content.ReadAsStringAsync();
            Assert.True(disabled.StatusCode == HttpStatusCode.OK, disabledBody);
            Assert.False(JsonNode.Parse(disabledBody)! ["isEnabled"]!.GetValue<bool>());

            // The explicit probe is server-side and must fail closed without a live charge when no active
            // credential exists. The response is a stable 502, and no secret is reflected.
            var connectionTest = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/merchants/{merchantA:D}/provider-accounts/{connectionId:D}/connection-tests");
            AddAdminHeaders(connectionTest, $"test-{runTag}", disabled.Headers.ETag!.Tag);
            using var tested = await client.SendAsync(connectionTest);
            Assert.Equal(HttpStatusCode.BadGateway, tested.StatusCode);
            Assert.DoesNotContain(secret, await tested.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var credentialRequest = credentialJson["request"]?.AsObject();
            Assert.NotNull(credentialRequest);
            var paymentSettingRequestId = Guid.Parse(credentialRequest!["approvalId"]!.ToString());
            Assert.Equal(approvalId, paymentSettingRequestId);

            using var requests = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/merchants/{merchantA:D}/payment-setting-requests?limit=100");
            Assert.Equal(HttpStatusCode.OK, requests.StatusCode);
            var requestsBody = await requests.Content.ReadAsStringAsync();
            Assert.Contains(paymentSettingRequestId.ToString("D"), requestsBody, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await using var cleanup = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConn);
            await Integration.Tests.IntegrationDb.ExecAsync(cleanup, """
                DELETE FROM admin.OperationRecords WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM txn.OutboxMessages WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM txn.PspConnections WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM merch.VaultSecretVersions WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM merch.Merchants WHERE Id IN (@merchantA,@merchantB);
                DELETE FROM admin.Users WHERE Id=@actor;
                """,
                ("@merchantA", merchantA), ("@merchantB", merchantB), ("@actor", Task8A1SqlFactory.AdminId));
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        AddAdminHeaders(request, $"read-{Guid.NewGuid():N}");
        return await client.SendAsync(request);
    }

    private static void AddAdminHeaders(HttpRequestMessage request, string key, string? etag = null)
    {
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, "csrf-b2");
        var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{cookieName}=csrf-b2");
        request.Headers.Add("Idempotency-Key", key);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }
}
