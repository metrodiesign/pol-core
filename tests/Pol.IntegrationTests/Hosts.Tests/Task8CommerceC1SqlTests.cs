extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Hosts.Tests;

[Trait("Capability", "ApiOperations")]
[Collection("ApiOperationsSql")]
[Trait("Category", "Integration")]
public sealed class Task8CommerceC1SqlTests
{
    [Fact]
    [Trait("Requirement", "REQ-3")]
    [Trait("Requirement", "REQ-10")]
    public async Task Canonical_order_children_and_transaction_support_resolve_parent_and_replay_review()
    {
        using var factory = new C1SqlFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        var orderId = Guid.CreateVersion7();
        var transactionId = Guid.CreateVersion7();
        var providerAccountId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        var runTag = Guid.NewGuid().ToString("N");

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConn))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantA, $"c1a-{Guid.NewGuid():N}"[..20]);
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantB, $"c1b-{Guid.NewGuid():N}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT shop.Orders
                    (Id, MerchantId, CreatedByAccountId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                     IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                     CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                     SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                     OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
                VALUES (@order, @merchant, @actor, @orderNo, 7, 1, @at, @at, 1,
                        0, NULL, NULL, NULL, NULL, N'C1 customer', N'0800000000',
                        100.00, 'THB', 100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'c1');
                INSERT txn.Transactions
                    (Id, MerchantId, OrderId, TransactionNo, AttemptNo,
                     AmountAmount, AmountCurrency, PaymentMethod, Provider, ProviderAccountId,
                     Environment, CredentialVersionId, ConfigurationVersion,
                     ProviderRequestReference, ProviderReference, RedirectUrl, ReturnBinding,
                     Status, ProviderStatus, OrderSnapshot, SafeProviderMetadata, NeedsReview, ReviewCode,
                     CreatedAt, UpdatedAt, SucceededAt, LastInquiryAt, NextInquiryAt, InquiryAttempts, Version)
                VALUES (@transaction, @merchant, @order, @transactionNo, 1,
                        100.00, 'THB', 'card', 1, @provider,
                        1, @credential, 1, @requestReference, NULL, NULL, NULL,
                        1, N'created', N'{"schemaVersion":1,"provenance":"CAPTURED_AT_CONFIRM"}', NULL, 0, NULL,
                        @at, @at, NULL, NULL, NULL, 0, 1);
                """,
                ("@order", orderId), ("@merchant", merchantA), ("@actor", Task8A1SqlFactory.AdminId),
                ("@orderNo", $"C1{orderId:N}"[..12]), ("@at", now),
                ("@transaction", transactionId), ("@transactionNo", $"TXN-{transactionId:N}"),
                ("@provider", providerAccountId), ("@credential", Guid.CreateVersion7()),
                ("@requestReference", $"request-{transactionId:N}"));
        }

        try
        {
            await using var versionProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConn);
            var currentOrderVersion = Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(
                versionProbe, "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", orderId)));
            var orderEtag = $"\"v{currentOrderVersion}\"";
            var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "c1",
                    items = new[] { new { productReference = "SKU-C1", quantity = 1 } },
                    ownerSaleId = (Guid?)null,
                    ownerBranchId = (Guid?)null,
                }),
            };
            AddAdminHeaders(patch, $"c1-order-patch-{runTag}", orderEtag);
            using var patched = await client.SendAsync(patch);
            var patchedBody = await patched.Content.ReadAsStringAsync();
            Assert.True(patched.StatusCode == HttpStatusCode.OK, $"{patchedBody}; expected={orderEtag}");

            var replayPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "c1",
                    items = new[] { new { productReference = "SKU-C1", quantity = 1 } },
                    ownerSaleId = (Guid?)null,
                    ownerBranchId = (Guid?)null,
                }),
            };
            AddAdminHeaders(replayPatch, $"c1-order-patch-{runTag}", orderEtag);
            using var replayedPatch = await client.SendAsync(replayPatch);
            Assert.Equal(HttpStatusCode.OK, replayedPatch.StatusCode);
            Assert.Equal(JsonNode.Parse(patchedBody)!.ToJsonString(),
                JsonNode.Parse(await replayedPatch.Content.ReadAsStringAsync())!.ToJsonString());

            var changedIntent = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "c1-changed",
                    items = new[] { new { productReference = "SKU-C1-CHANGED", quantity = 1 } },
                    ownerSaleId = (Guid?)null,
                    ownerBranchId = (Guid?)null,
                }),
            };
            AddAdminHeaders(changedIntent, $"c1-order-patch-{runTag}", orderEtag);
            using var changedIntentResponse = await client.SendAsync(changedIntent);
            Assert.Equal(HttpStatusCode.Conflict, changedIntentResponse.StatusCode);

            var stalePatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "c1-stale",
                    items = new[] { new { productReference = "SKU-C1-STALE", quantity = 1 } },
                    ownerSaleId = (Guid?)null,
                    ownerBranchId = (Guid?)null,
                }),
            };
            var staleKey = $"c1-order-stale-{runTag}";
            AddAdminHeaders(stalePatch, staleKey, orderEtag);
            using var stalePatchResponse = await client.SendAsync(stalePatch);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stalePatchResponse.StatusCode);

            await using (var rollbackProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConn))
            {
                var staleLedgerRows = await Integration.Tests.IntegrationDb.ScalarAsync(rollbackProbe,
                    "SELECT COUNT_BIG(*) FROM txn.AdminOperationRecords WHERE MerchantId=@merchant AND Operation=N'order.patch' AND IdempotencyKey=@key;",
                    ("@merchant", merchantA), ("@key", staleKey));
                Assert.Equal(0L, Convert.ToInt64(staleLedgerRows));
                var persistedVersion = await Integration.Tests.IntegrationDb.ScalarAsync(rollbackProbe,
                    "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", orderId));
                Assert.Equal(2L, Convert.ToInt64(persistedVersion));
                var persistedProduct = await Integration.Tests.IntegrationDb.ScalarAsync(rollbackProbe,
                    "SELECT ProductCode FROM shop.OrderItems WHERE OrderId=@order;", ("@order", orderId));
                Assert.Equal("SKU-C1", persistedProduct?.ToString());
            }

            using var items = await SendAsync(client, HttpMethod.Get, $"/api/v1/orders/{orderId:D}/items");
            Assert.Equal(HttpStatusCode.OK, items.StatusCode);
            using var history = await SendAsync(client, HttpMethod.Get, $"/api/v1/orders/{orderId:D}/history");
            Assert.Equal(HttpStatusCode.OK, history.StatusCode);

            using var transactions = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/transactions?merchantId={merchantA:D}");
            Assert.Equal(HttpStatusCode.OK, transactions.StatusCode);
            Assert.Contains(transactionId.ToString("D"), await transactions.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            using var detail = await SendAsync(client, HttpMethod.Get, $"/api/v1/transactions/{transactionId:D}?merchantId={merchantA:D}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var transactionEtag = detail.Headers.ETag!.Tag;
            using var crossMerchant = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/transactions/{transactionId:D}?merchantId={merchantB:D}");
            Assert.Equal(HttpStatusCode.NotFound, crossMerchant.StatusCode);

            using var unauthenticatedCheckoutMethods = await client.GetAsync("/api/v1/checkout/payment-methods");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedCheckoutMethods.StatusCode);

            using var events = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/transactions/{transactionId:D}/events?merchantId={merchantA:D}");
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);

            var verify = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/transactions/{transactionId:D}/verify");
            verify.RequestUri = new Uri($"/api/v1/transactions/{transactionId:D}/verify?merchantId={merchantA:D}", UriKind.Relative);
            AddAdminHeaders(verify, $"c1-verify-{runTag}", transactionEtag);
            using var verified = await client.SendAsync(verify);
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);

            var review = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/transactions/{transactionId:D}/review-notes?merchantId={merchantA:D}")
            {
                Content = JsonContent.Create(new { note = "C1 support review" }),
            };
            AddAdminHeaders(review, $"c1-review-{runTag}", transactionEtag);
            using var reviewed = await client.SendAsync(review);
            Assert.Equal(HttpStatusCode.OK, reviewed.StatusCode);
            Assert.Contains("C1 support review", await reviewed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var replay = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/transactions/{transactionId:D}/review-notes?merchantId={merchantA:D}")
            {
                Content = JsonContent.Create(new { note = "C1 support review" }),
            };
            AddAdminHeaders(replay, $"c1-review-{runTag}", transactionEtag);
            using var replayed = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

            var stale = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/transactions/{transactionId:D}/review-notes?merchantId={merchantA:D}")
            {
                Content = JsonContent.Create(new { note = "stale" }),
            };
            AddAdminHeaders(stale, $"c1-stale-{runTag}", "\"v0\"");
            using var staleResponse = await client.SendAsync(stale);
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleResponse.StatusCode);
        }
        finally
        {
            await using var cleanup = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConn);
            await Integration.Tests.IntegrationDb.ExecAsync(cleanup, """
                DELETE FROM txn.AdminOperationRecords WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM txn.TransactionEvents WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM txn.Transactions WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM shop.Orders WHERE MerchantId IN (@merchantA,@merchantB);
                DELETE FROM merch.Merchants WHERE Id IN (@merchantA,@merchantB);
                """,
                ("@merchantA", merchantA), ("@merchantB", merchantB));
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        AddAdminHeaders(request, $"c1-read-{Guid.NewGuid():N}");
        return await client.SendAsync(request);
    }

    private static void AddAdminHeaders(HttpRequestMessage request, string key, string? etag = null)
    {
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, "csrf-c1");
        var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{cookieName}=csrf-c1");
        request.Headers.Add("Idempotency-Key", key);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }
}

file sealed class C1SqlFactory : Task8A1SqlFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITrustedOrderPricingSource>();
            services.AddScoped<ITrustedOrderPricingSource, C1TrustedPricing>();
        });
    }
}

file sealed class C1TrustedPricing : ITrustedOrderPricingSource
{
    public Task<TrustedOrderPricing> PriceAsync(
        Guid merchantId, string businessType, IReadOnlyList<OrderItemRequest> requestedItems,
        CancellationToken cancellationToken)
    {
        var lines = requestedItems.Select(item => new TrustedOrderLineInput(
            item.ProductReference, "card", "C1 product", item.Quantity,
            Money.Of(100m, "THB"), Money.Of(0m, "THB"), Money.Of(0m, "THB"),
            Money.Of(100m * item.Quantity, "THB"), "task8-c1-test")).ToArray();
        return Task.FromResult(new TrustedOrderPricing(
            "THB", lines, Money.Of(0m, "THB"), Money.Of(0m, "THB")));
    }
}
