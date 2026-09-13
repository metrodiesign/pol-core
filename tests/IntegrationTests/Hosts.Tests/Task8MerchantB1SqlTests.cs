extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Hosts.Tests;

[Trait("Capability", "ApiOperations")]
[Collection("ApiOperationsSql")]
[Trait("Category", "Integration")]
public sealed class Task8MerchantB1SqlTests
{
    [Fact]
    [Trait("Requirement", "REQ-5")]
    [Trait("Requirement", "REQ-10")]
    public async Task Canonical_merchant_branch_and_sale_lifecycle_uses_real_sql_scope_etag_and_replay()
    {
        using var factory = new Task8A1SqlFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var branchA = Guid.Empty;
        var branchA2 = Guid.Empty;
        var branchB = Guid.Empty;
        var saleA = Guid.Empty;
        var correlation = $"task8-b1-{runTag}";

        await using (var connection = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConn))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(connection, merchantA, $"b1a-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(connection, merchantB, $"b1b-{runTag}"[..20]);
        }

        try
        {
            using var merchant = await SendAsync(client, HttpMethod.Get, $"/api/v1/merchants/{merchantA:D}");
            Assert.Equal(HttpStatusCode.OK, merchant.StatusCode);
            var merchantJson = JsonNode.Parse(await merchant.Content.ReadAsStringAsync())!.AsObject();
            Assert.Equal(merchantA.ToString("D"), merchantJson["id"]!.ToString(), ignoreCase: true);
            var merchantEtag = merchant.Headers.ETag!.Tag;

            var merchantPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/merchants/{merchantA:D}")
            {
                Content = JsonContent.Create(new { name = "B1 Merchant", status = "active" }),
            };
            AddAdminHeaders(merchantPatch, $"merchant-patch-{runTag}", merchantEtag, correlation);
            using var patchedMerchant = await client.SendAsync(merchantPatch);
            Assert.Equal(HttpStatusCode.OK, patchedMerchant.StatusCode);
            var patchedMerchantEtag = patchedMerchant.Headers.ETag!.Tag;

            var merchantReplay = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/merchants/{merchantA:D}")
            {
                Content = JsonContent.Create(new { name = "B1 Merchant", status = "active" }),
            };
            AddAdminHeaders(merchantReplay, $"merchant-patch-{runTag}", merchantEtag, correlation);
            using var replayedMerchant = await client.SendAsync(merchantReplay);
            Assert.Equal(HttpStatusCode.OK, replayedMerchant.StatusCode);
            Assert.Equal(
                JsonNode.Parse(await patchedMerchant.Content.ReadAsStringAsync())!["id"]!.ToString(),
                JsonNode.Parse(await replayedMerchant.Content.ReadAsStringAsync())!["id"]!.ToString());

            var changedMerchant = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/merchants/{merchantA:D}")
            {
                Content = JsonContent.Create(new { name = "B1 Changed", status = "active" }),
            };
            AddAdminHeaders(changedMerchant, $"merchant-patch-{runTag}", merchantEtag, correlation);
            using var changedMerchantResponse = await client.SendAsync(changedMerchant);
            Assert.Equal(HttpStatusCode.Conflict, changedMerchantResponse.StatusCode);

            var staleMerchant = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/merchants/{merchantA:D}")
            {
                Content = JsonContent.Create(new { name = "B1 Stale", status = "active" }),
            };
            var staleMerchantKey = $"merchant-stale-{runTag}";
            AddAdminHeaders(staleMerchant, staleMerchantKey, merchantEtag, correlation);
            using var staleMerchantResponse = await client.SendAsync(staleMerchant);
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleMerchantResponse.StatusCode);

            await using (var rollbackProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConn))
            {
                var staleLedgerRows = await Integration.Tests.IntegrationDb.ScalarAsync(rollbackProbe,
                    "SELECT COUNT_BIG(*) FROM admin.OperationRecords WHERE MerchantId=@merchant AND Operation=N'merchant.patch' AND IdempotencyKey=@key;",
                    ("@merchant", merchantA), ("@key", staleMerchantKey));
                Assert.Equal(0L, Convert.ToInt64(staleLedgerRows));
                var persistedName = await Integration.Tests.IntegrationDb.ScalarAsync(rollbackProbe,
                    "SELECT Name FROM merch.Merchants WHERE Id=@merchant;", ("@merchant", merchantA));
                Assert.Equal("B1 Merchant", persistedName?.ToString());
            }

            (branchA, var branchAResponse) = await CreateBranchAsync(client, merchantA, "branch-a", "Branch A", runTag);
            using var branchAResponseScope = branchAResponse;
            Assert.Equal(HttpStatusCode.Created, branchAResponse.StatusCode);
            var branchAEtag = branchAResponse.Headers.ETag!.Tag;

            var branchReplay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/merchants/{merchantA:D}/branches")
            {
                Content = JsonContent.Create(new { code = "branch-a", name = "Branch A" }),
            };
            AddAdminHeaders(branchReplay, $"branch-create-branch-a-{runTag}");
            using var replayedBranch = await client.SendAsync(branchReplay);
            Assert.Equal(HttpStatusCode.Created, replayedBranch.StatusCode);
            Assert.Equal(branchA.ToString("D"), JsonNode.Parse(await replayedBranch.Content.ReadAsStringAsync())!["branchId"]!.ToString(), ignoreCase: true);

            var changedBranch = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/merchants/{merchantA:D}/branches")
            {
                Content = JsonContent.Create(new { code = "branch-a", name = "Changed" }),
            };
            AddAdminHeaders(changedBranch, $"branch-create-branch-a-{runTag}");
            using var changedBranchResponse = await client.SendAsync(changedBranch);
            Assert.Equal(HttpStatusCode.Conflict, changedBranchResponse.StatusCode);

            (branchA2, var branchA2Response) = await CreateBranchAsync(client, merchantA, "branch-a2", "Branch A2", runTag);
            branchA2Response.Dispose();
            (branchB, var branchBResponse) = await CreateBranchAsync(client, merchantB, "branch-b", "Branch B", runTag);
            branchBResponse.Dispose();

            using var branches = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/merchants/{merchantA:D}/branches?limit=100");
            Assert.Equal(HttpStatusCode.OK, branches.StatusCode);
            Assert.Contains(branchA.ToString("D"), await branches.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(branchB.ToString("D"), await branches.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            var crossMerchantSale = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/merchants/{merchantA:D}/sales")
            {
                Content = JsonContent.Create(new { branchId = branchB, code = "sale-cross", name = "Cross merchant" }),
            };
            AddAdminHeaders(crossMerchantSale, $"sale-cross-{runTag}");
            using var crossMerchantResponse = await client.SendAsync(crossMerchantSale);
            Assert.Equal(HttpStatusCode.BadRequest, crossMerchantResponse.StatusCode);

            var createSale = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/merchants/{merchantA:D}/sales")
            {
                Content = JsonContent.Create(new { branchId = branchA, code = "sale-a", name = "Sale A" }),
            };
            AddAdminHeaders(createSale, $"sale-create-{runTag}");
            using var saleCreated = await client.SendAsync(createSale);
            var saleBody = await saleCreated.Content.ReadAsStringAsync();
            Assert.True(saleCreated.StatusCode == HttpStatusCode.Created, saleBody);
            saleA = Guid.Parse(JsonNode.Parse(saleBody)! ["saleId"]!.ToString());
            var saleEtag = saleCreated.Headers.ETag!.Tag;

            var moveSale = new HttpRequestMessage(HttpMethod.Patch,
                $"/api/v1/merchants/{merchantA:D}/sales/{saleA:D}")
            {
                Content = JsonContent.Create(new { branchId = branchA2, reason = "trusted master reassignment" }),
            };
            AddAdminHeaders(moveSale, $"sale-move-{runTag}", saleEtag);
            using var movedSale = await client.SendAsync(moveSale);
            var movedBody = await movedSale.Content.ReadAsStringAsync();
            Assert.True(movedSale.StatusCode == HttpStatusCode.OK, movedBody);
            Assert.Equal(branchA2.ToString("D"), JsonNode.Parse(movedBody)! ["branchId"]!.ToString(), ignoreCase: true);
            var movedEtag = movedSale.Headers.ETag!.Tag;

            var staleMove = new HttpRequestMessage(HttpMethod.Patch,
                $"/api/v1/merchants/{merchantA:D}/sales/{saleA:D}")
            {
                Content = JsonContent.Create(new { branchId = branchA, reason = "stale request" }),
            };
            AddAdminHeaders(staleMove, $"sale-stale-{runTag}", saleEtag, correlation);
            using var staleResponse = await client.SendAsync(staleMove);
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
            Assert.Equal(correlation, staleResponse.Headers.GetValues("X-Correlation-ID").Single());

            using var sales = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/merchants/{merchantA:D}/sales?limit=100");
            Assert.Equal(HttpStatusCode.OK, sales.StatusCode);
            var salesText = await sales.Content.ReadAsStringAsync();
            Assert.Contains(branchA2.ToString("D"), salesText, StringComparison.OrdinalIgnoreCase);

            var branchPatch = new HttpRequestMessage(HttpMethod.Patch,
                $"/api/v1/merchants/{merchantA:D}/branches/{branchA:D}")
            {
                Content = JsonContent.Create(new { name = "Branch A Renamed" }),
            };
            AddAdminHeaders(branchPatch, $"branch-patch-{runTag}", branchAEtag);
            using var branchPatched = await client.SendAsync(branchPatch);
            Assert.Equal(HttpStatusCode.OK, branchPatched.StatusCode);

            await using var probe = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConn);
            var operationCount = await Integration.Tests.IntegrationDb.ScalarAsync(probe,
                "SELECT COUNT_BIG(*) FROM admin.OperationRecords WHERE ActorId=@actor AND MerchantId=@merchant AND Operation IN (N'merchant.patch',N'branch.create',N'branch.patch',N'sale.create',N'sale.patch');",
                ("@actor", Task8A1SqlFactory.AdminId), ("@merchant", merchantA));
            Assert.True(Convert.ToInt64(operationCount) >= 6);
            Assert.NotEqual(saleEtag, movedEtag);
        }
        finally
        {
            await using var cleanup = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConn);
            await Integration.Tests.IntegrationDb.ExecAsync(cleanup, """
                DELETE FROM admin.OperationRecords WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM merch.Sales WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM merch.Branches WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM merch.Merchants WHERE Id IN (@merchantA,@merchantB);
                """,
                ("@merchantA", merchantA), ("@merchantB", merchantB));
        }
    }

    private static async Task<(Guid Id, HttpResponseMessage Response)> CreateBranchAsync(
        HttpClient client, Guid merchantId, string code, string name, string runTag)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/merchants/{merchantId:D}/branches")
        {
            Content = JsonContent.Create(new { code, name }),
        };
        AddAdminHeaders(request, $"branch-create-{code}-{runTag}");
        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return (Guid.Parse(JsonNode.Parse(body)! ["branchId"]!.ToString()), response);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        AddAdminHeaders(request, $"read-{Guid.NewGuid():N}");
        return await client.SendAsync(request);
    }

    private static void AddAdminHeaders(
        HttpRequestMessage request, string key, string? etag = null, string? correlation = null)
    {
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, "csrf-b1");
        var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{cookieName}=csrf-b1");
        request.Headers.Add("Idempotency-Key", key);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
        if (correlation is not null)
            request.Headers.Add("X-Correlation-ID", correlation);
    }
}
