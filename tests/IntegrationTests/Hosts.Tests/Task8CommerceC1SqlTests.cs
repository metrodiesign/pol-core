extern alias ApiHost;
using System.Net;
using System.Security.Claims;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application;
using Orders.Domain;
using SharedKernel;
using ApiIdentity = ApiHost::Api.IdentityAccess;

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
        var database = NewReviewFixDatabaseName();
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        using var factory = new C1SqlFactory(database);
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
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantA, $"c1a-{Guid.NewGuid():N}"[..20]);
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantB, $"c1b-{Guid.NewGuid():N}"[..20]);
            await SeedIdentityAccessAsync(seed, merchantA, "c1-parent");
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
                Integration.Tests.IntegrationDb.SaConnFor(database));
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
            AddAdminHeadersWithoutIdempotency(patch, orderEtag);
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
            AddAdminHeadersWithoutIdempotency(replayPatch, orderEtag);
            using var replayedPatch = await client.SendAsync(replayPatch);
            Assert.Equal(HttpStatusCode.OK, replayedPatch.StatusCode);
            Assert.Equal(JsonNode.Parse(patchedBody)!.ToJsonString(),
                JsonNode.Parse(await replayedPatch.Content.ReadAsStringAsync())!.ToJsonString());

            await using (var patchAudit = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                var internalKey = $"order.patch:{orderId:D}:v{currentOrderVersion}";
                Assert.Equal(1L, Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(
                    patchAudit,
                    "SELECT COUNT_BIG(*) FROM txn.AdminOperationRecords WHERE MerchantId=@merchant AND Operation=N'order.patch' AND IdempotencyKey=@key;",
                    ("@merchant", merchantA), ("@key", internalKey))));
            }

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
            AddAdminHeadersWithoutIdempotency(changedIntent, orderEtag);
            using var changedIntentResponse = await client.SendAsync(changedIntent);
            Assert.Equal(HttpStatusCode.Conflict, changedIntentResponse.StatusCode);

            var changedMetadata = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId:D}")
            {
                Content = JsonContent.Create(new
                {
                    metadata = new
                    {
                        schemaVersion = 1,
                        data = new { changed = true },
                    },
                }),
            };
            AddAdminHeadersWithoutIdempotency(changedMetadata, orderEtag);
            using var changedMetadataResponse = await client.SendAsync(changedMetadata);
            Assert.Equal(HttpStatusCode.Conflict, changedMetadataResponse.StatusCode);

            var changedDiscount = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId:D}")
            {
                Content = JsonContent.Create(new { orderDiscountAmount = "1.0000" }),
            };
            AddAdminHeadersWithoutIdempotency(changedDiscount, orderEtag);
            using var changedDiscountResponse = await client.SendAsync(changedDiscount);
            Assert.Equal(HttpStatusCode.Conflict, changedDiscountResponse.StatusCode);

            var changedCharge = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/orders/{orderId:D}")
            {
                Content = JsonContent.Create(new { orderChargeAmount = "1.0000" }),
            };
            AddAdminHeadersWithoutIdempotency(changedCharge, orderEtag);
            using var changedChargeResponse = await client.SendAsync(changedCharge);
            Assert.Equal(HttpStatusCode.Conflict, changedChargeResponse.StatusCode);

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
            AddAdminHeadersWithoutIdempotency(stalePatch, "\"v999\"");
            using var stalePatchResponse = await client.SendAsync(stalePatch);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stalePatchResponse.StatusCode);

            await using (var rollbackProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                var staleLedgerRows = await Integration.Tests.IntegrationDb.ScalarAsync(rollbackProbe,
                    "SELECT COUNT_BIG(*) FROM txn.AdminOperationRecords WHERE MerchantId=@merchant AND Operation=N'order.patch' AND IdempotencyKey=@key;",
                    ("@merchant", merchantA), ("@key", $"order.patch:{orderId:D}:v999"));
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
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.9")]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Canonical_patch_notification_intent_is_presence_aware_and_issue_enqueues_once()
    {
        var database = NewReviewFixDatabaseName();
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        using var factory = new C1SqlFactory(database);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var merchantId = Guid.CreateVersion7();
        factory.Identity.MerchantId = merchantId;
        var runTag = Guid.NewGuid().ToString("N");
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"patch-intent-{runTag}"[..20]);
            await SeedIdentityAccessAsync(seed, merchantId, "c1-patch-intent");
        }

        try
        {
            using var create = CanonicalCreateRequest(
                merchantId, $"patch-intent-true-{runTag}", includeIssueNow: false);
            using var createdResponse = await client.SendAsync(create);
            var createdBody = await createdResponse.Content.ReadAsStringAsync();
            Assert.True(createdResponse.StatusCode == HttpStatusCode.Created, createdBody);
            using var created = JsonDocument.Parse(createdBody);
            var orderId = created.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
            var version = created.RootElement.GetProperty("order").GetProperty("version").GetInt64();

            using var detail = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}");
            AddAdminHeaders(detail, $"patch-intent-detail-{runTag}");
            using var detailResponse = await client.SendAsync(detail);
            Assert.True(detailResponse.StatusCode == HttpStatusCode.OK,
                await detailResponse.Content.ReadAsStringAsync());
            Guid itemId;
            await using (var auditProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                itemId = Guid.Parse((await Integration.Tests.IntegrationDb.ScalarAsync(
                    auditProbe, "SELECT Id FROM shop.OrderItems WHERE OrderId=@order;", ("@order", orderId)))!.ToString()!);
                Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    auditProbe,
                    "SELECT COUNT(*) FROM shop.OrderItemRevealAudits WHERE OrderItemId=@item AND MerchantId=@merchant;",
                    ("@item", itemId), ("@merchant", merchantId))));
            }

            using var enable = PatchNotificationIntent(
                merchantId, orderId, version, $"patch-intent-enable-{runTag}",
                send: true, email: "patch@example.test", phone: "+66811111111");
            using var enabledResponse = await client.SendAsync(enable);
            var enabledBody = await enabledResponse.Content.ReadAsStringAsync();
            Assert.True(enabledResponse.StatusCode == HttpStatusCode.OK, enabledBody);
            using var enabled = JsonDocument.Parse(enabledBody);
            var enabledVersion = enabled.RootElement.GetProperty("version").GetInt64();

            await using (var probe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT NotifyOnIssue FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
                Assert.Equal("patch@example.test", (await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT NotificationEmail FROM shop.Orders WHERE Id=@order;", ("@order", orderId)))?.ToString());
                Assert.Equal("+66811111111", (await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT NotificationPhoneNumber FROM shop.Orders WHERE Id=@order;", ("@order", orderId)))?.ToString());
                Assert.Equal("insurance", (await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT BusinessType FROM shop.Orders WHERE Id=@order;", ("@order", orderId)))?.ToString());
                Assert.Equal(100m, Convert.ToDecimal(await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT AmountAmount FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
                Assert.True((await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT Metadata FROM shop.Orders WHERE Id=@order;", ("@order", orderId))) is null or DBNull);
                Assert.Equal("SKU-CANONICAL", (await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT ProductCode FROM shop.OrderItems WHERE OrderId=@order;", ("@order", orderId)))?.ToString());
                Assert.True((await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT OwnerSaleId FROM shop.Orders WHERE Id=@order;", ("@order", orderId))) is null or DBNull);
                Assert.Equal(itemId, Guid.Parse((await Integration.Tests.IntegrationDb.ScalarAsync(
                    probe, "SELECT Id FROM shop.OrderItems WHERE OrderId=@order;", ("@order", orderId)))!.ToString()!));
            }

            using var metadataOnly = PatchNotificationIntent(
                merchantId, orderId, enabledVersion, $"patch-intent-metadata-{runTag}",
                send: null, email: null, phone: null,
                metadata: new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["data"] = new JsonObject { ["source"] = "metadata-only" },
                });
            using var metadataOnlyResponse = await client.SendAsync(metadataOnly);
            var metadataOnlyBody = await metadataOnlyResponse.Content.ReadAsStringAsync();
            Assert.True(metadataOnlyResponse.StatusCode == HttpStatusCode.OK, metadataOnlyBody);
            using var metadataOnlyJson = JsonDocument.Parse(metadataOnlyBody);
            Assert.Equal("metadata-only", metadataOnlyJson.RootElement.GetProperty("metadata")
                .GetProperty("data").GetProperty("source").GetString());
            var metadataOnlyVersion = metadataOnlyJson.RootElement.GetProperty("version").GetInt64();

            var businessReprice = new HttpRequestMessage(
                HttpMethod.Patch, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { businessType = "insurance" }),
            };
            businessReprice.Headers.Add("If-Match", $"\"v{metadataOnlyVersion}\"");
            AddIdentityBearer(businessReprice);
            using var businessRepriceResponse = await client.SendAsync(businessReprice);
            var businessRepriceBody = await businessRepriceResponse.Content.ReadAsStringAsync();
            Assert.True(businessRepriceResponse.StatusCode == HttpStatusCode.OK, businessRepriceBody);
            using var businessRepriceJson = JsonDocument.Parse(businessRepriceBody);
            var businessRepriceVersion = businessRepriceJson.RootElement.GetProperty("version").GetInt64();
            await using (var repriceProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(itemId, Guid.Parse((await Integration.Tests.IntegrationDb.ScalarAsync(
                    repriceProbe, "SELECT Id FROM shop.OrderItems WHERE OrderId=@order;", ("@order", orderId)))!.ToString()!));
                Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    repriceProbe,
                    "SELECT COUNT(*) FROM shop.OrderItemRevealAudits a JOIN shop.OrderItems i ON i.Id=a.OrderItemId WHERE a.OrderItemId=@item AND i.OrderId=@order;",
                    ("@item", itemId), ("@order", orderId))));
            }

            using var oversize = PatchNotificationIntent(
                merchantId, orderId, businessRepriceVersion, $"patch-intent-oversize-{runTag}",
                send: true, email: new string('a', 321) + "@example.test", phone: null);
            using var oversizeResponse = await client.SendAsync(oversize);
            Assert.Equal(HttpStatusCode.BadRequest, oversizeResponse.StatusCode);
            Assert.Contains("validation_failed", await oversizeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var oversizePhone = PatchNotificationIntent(
                merchantId, orderId, businessRepriceVersion, $"patch-intent-phone-oversize-{runTag}",
                send: true, email: null, phone: new string('1', 33));
            using var oversizePhoneResponse = await client.SendAsync(oversizePhone);
            Assert.Equal(HttpStatusCode.BadRequest, oversizePhoneResponse.StatusCode);
            Assert.Contains("validation_failed", await oversizePhoneResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            await using (var invalidProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
                Assert.Equal(businessRepriceVersion, Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(
                invalidProbe, "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));

            using var omitted = PatchNotificationIntent(
                merchantId, orderId, businessRepriceVersion, $"patch-intent-omitted-{runTag}",
                send: null, email: null, phone: null);
            using var omittedResponse = await client.SendAsync(omitted);
            var omittedBody = await omittedResponse.Content.ReadAsStringAsync();
            Assert.True(omittedResponse.StatusCode == HttpStatusCode.OK, omittedBody);
            using var omittedJson = JsonDocument.Parse(omittedBody);
            var omittedVersion = omittedJson.RootElement.GetProperty("version").GetInt64();

            using var issue = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/issue?merchantId={merchantId:D}");
            AddAdminHeaders(issue, $"patch-intent-issue-{runTag}", $"\"v{omittedVersion}\"");
            using var issued = await client.SendAsync(issue);
            Assert.True(issued.StatusCode == HttpStatusCode.OK,
                await issued.Content.ReadAsStringAsync());

            var secondCreate = CanonicalCreateRequest(
                merchantId, $"patch-intent-false-{runTag}", includeIssueNow: false);
            using (secondCreate)
            using (var secondResponse = await client.SendAsync(secondCreate))
            {
                var secondBody = await secondResponse.Content.ReadAsStringAsync();
                Assert.True(secondResponse.StatusCode == HttpStatusCode.Created, secondBody);
                using var second = JsonDocument.Parse(secondBody);
                var secondOrderId = second.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
                var secondVersion = second.RootElement.GetProperty("order").GetProperty("version").GetInt64();

                using var disable = PatchNotificationIntent(
                    merchantId, secondOrderId, secondVersion, $"patch-intent-disable-{runTag}",
                    send: false, email: "ignored@example.test", phone: "+66812222222");
                using var disabledResponse = await client.SendAsync(disable);
                var disabledBody = await disabledResponse.Content.ReadAsStringAsync();
                Assert.True(disabledResponse.StatusCode == HttpStatusCode.OK, disabledBody);
                using var disabled = JsonDocument.Parse(disabledBody);
                var disabledVersion = disabled.RootElement.GetProperty("version").GetInt64();

                using var secondIssue = new HttpRequestMessage(
                    HttpMethod.Post, $"/api/v1/orders/{secondOrderId:D}/issue?merchantId={merchantId:D}");
                AddAdminHeaders(secondIssue, $"patch-intent-false-issue-{runTag}", $"\"v{disabledVersion}\"");
                using var secondIssued = await client.SendAsync(secondIssue);
                Assert.Equal(HttpStatusCode.OK, secondIssued.StatusCode);

                await using var secondProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                    Integration.Tests.IntegrationDb.SaConnFor(database));
                Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    secondProbe, "SELECT NotifyOnIssue FROM shop.Orders WHERE Id=@order;", ("@order", secondOrderId))));
                Assert.True((await Integration.Tests.IntegrationDb.ScalarAsync(
                    secondProbe, "SELECT NotificationEmail FROM shop.Orders WHERE Id=@order;", ("@order", secondOrderId))) is null or DBNull);
                Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    secondProbe, "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant AND Type=N'PaymentLinkNotificationRequestedV1';",
                    ("@merchant", merchantId))));
            }

            using var missingCreate = CanonicalCreateRequest(
                merchantId, $"patch-intent-missing-{runTag}", includeIssueNow: false);
            using var missingCreateResponse = await client.SendAsync(missingCreate);
            var missingCreateBody = await missingCreateResponse.Content.ReadAsStringAsync();
            Assert.True(missingCreateResponse.StatusCode == HttpStatusCode.Created, missingCreateBody);
            using var missingCreated = JsonDocument.Parse(missingCreateBody);
            var missingOrderId = missingCreated.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
            var missingVersion = missingCreated.RootElement.GetProperty("order").GetProperty("version").GetInt64();
            using var missing = PatchNotificationIntent(
                merchantId, missingOrderId, missingVersion, $"patch-intent-missing-patch-{runTag}",
                send: true, email: null, phone: null);
            using var missingResponse = await client.SendAsync(missing);
            Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
            Assert.Contains("notification_recipient_required",
                await missingResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await using var finalProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", missingOrderId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant AND Type=N'PaymentLinkNotificationRequestedV1';",
                ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.8")]
    [Trait("Requirement", "REQ-6.9")]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Canonical_create_order_request_reaches_the_trusted_order_workflow()
    {
        var database = NewReviewFixDatabaseName();
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        using var factory = new C1SqlFactory(database);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var merchantId = Guid.CreateVersion7();
        factory.Identity.MerchantId = merchantId;
        var runTag = Guid.NewGuid().ToString("N");
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"canonical-{Guid.NewGuid():N}"[..20]);
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
            await SeedIdentityAccessAsync(seed, merchantId, "c1-draft");

        try
        {
            using (var identityProbe = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me"))
            {
                AddIdentityBearer(identityProbe);
                using var identityResponse = await client.SendAsync(identityProbe);
                Assert.True(identityResponse.StatusCode == HttpStatusCode.OK,
                    await identityResponse.Content.ReadAsStringAsync());
            }
            var createKey = $"canonical-create-{runTag}";
            var draftMetadata = new
            {
                schemaVersion = 1,
                data = new { persistent = "yes" },
            };
            var request = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "insurance",
                    currency = "THB",
                    items = new[]
                    {
                        new
                        {
                            productReference = "SKU-CANONICAL",
                            productCode = "SKU-CANONICAL",
                            productName = "C1 product",
                            quantity = 1,
                            unitPrice = "100.0000",
                            discountAmount = "0.0000",
                            taxAmount = "0.0000",
                            lineAmount = "100.0000",
                        },
                    },
                    metadata = draftMetadata,
                    notificationIntent = new
                    {
                        send = true,
                        email = "draft-issue@example.test",
                        phoneNumber = "+66800000000",
                    },
                    issueNow = false,
                }),
            };
            AddAdminHeaders(request, createKey);
            AddIdentityBearer(request);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, body);
            using var created = JsonDocument.Parse(body);
            var orderId = created.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
            var draftVersion = created.RootElement.GetProperty("order").GetProperty("version").GetInt64();
            Assert.Equal("yes", created.RootElement.GetProperty("order").GetProperty("metadata")
                .GetProperty("data").GetProperty("persistent").GetString());
            Assert.Equal("Draft", created.RootElement.GetProperty("order").GetProperty("orderStatus").GetString());
            Assert.Null(created.RootElement.GetProperty("paymentLink").ValueKind == JsonValueKind.Null
                ? null
                : created.RootElement.GetProperty("paymentLink"));

            using var draftReplay = CanonicalCreateRequest(
                merchantId, createKey, includeIssueNow: false,
                metadata: JsonNode.Parse(JsonSerializer.SerializeToNode(draftMetadata)!.ToJsonString())!.AsObject(),
                notify: true, notifyEmail: "draft-issue@example.test");
            using var draftReplayResponse = await client.SendAsync(draftReplay);
            Assert.True(draftReplayResponse.StatusCode == HttpStatusCode.Created,
                await draftReplayResponse.Content.ReadAsStringAsync());
            using var draftReplayDocument = JsonDocument.Parse(await draftReplayResponse.Content.ReadAsStringAsync());
            Assert.Equal(orderId, draftReplayDocument.RootElement.GetProperty("order").GetProperty("orderId").GetGuid());

            using var draftChanged = CanonicalCreateRequest(
                merchantId, createKey, includeIssueNow: false, productReference: "SKU-DRAFT-CHANGED");
            using var draftChangedResponse = await client.SendAsync(draftChanged);
            Assert.Equal(HttpStatusCode.Conflict, draftChangedResponse.StatusCode);

            await using (var draftProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(7, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    draftProbe, "SELECT Status FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
                Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    draftProbe, "SELECT COUNT(*) FROM checkout.PaymentLinks WHERE OrderId=@order;", ("@order", orderId))));
                Assert.Equal(100m, Convert.ToDecimal(await Integration.Tests.IntegrationDb.ScalarAsync(
                    draftProbe, "SELECT AmountAmount FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
                Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                    draftProbe, "SELECT COUNT(*) FROM txn.AdminOperationRecords WHERE MerchantId=@merchant;",
                    ("@merchant", merchantId))));
            }

            var patch = new HttpRequestMessage(
                HttpMethod.Patch, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "insurance",
                    items = new[] { new { productReference = "SKU-PATCH", quantity = 1 } },
                    ownerSaleId = (Guid?)null,
                    ownerBranchId = (Guid?)null,
                }),
            };
            AddAdminHeaders(patch, $"canonical-patch-{runTag}", $"\"v{draftVersion}\"");
            using var patchedResponse = await client.SendAsync(patch);
            var patchedBody = await patchedResponse.Content.ReadAsStringAsync();
            Assert.True(patchedResponse.StatusCode == HttpStatusCode.OK, patchedBody);
            using var patched = JsonDocument.Parse(patchedBody);
            var patchedVersion = patched.RootElement.GetProperty("version").GetInt64();
            Assert.Equal("yes", patched.RootElement.GetProperty("metadata")
                .GetProperty("data").GetProperty("persistent").GetString());

            var issue = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/issue?merchantId={merchantId:D}");
            AddAdminHeaders(issue, $"canonical-issue-{runTag}", $"\"v{patchedVersion}\"");
            using var issuedResponse = await client.SendAsync(issue);
            var issuedBody = await issuedResponse.Content.ReadAsStringAsync();
            Assert.True(issuedResponse.StatusCode == HttpStatusCode.OK, issuedBody);
            using var issued = JsonDocument.Parse(issuedBody);
            var issuedVersion = issued.RootElement.GetProperty("order").GetProperty("version").GetInt64();
            Assert.Equal("Open", issued.RootElement.GetProperty("order").GetProperty("orderStatus").GetString());
            Assert.NotEqual(JsonValueKind.Null, issued.RootElement.GetProperty("rawToken").ValueKind);

            var rotate = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/payment-links?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { sendNotification = true }),
            };
            AddAdminHeaders(rotate, $"canonical-rotate-{runTag}", $"\"v{issuedVersion}\"");
            using var rotatedResponse = await client.SendAsync(rotate);
            var rotatedBody = await rotatedResponse.Content.ReadAsStringAsync();
            Assert.True(rotatedResponse.StatusCode == HttpStatusCode.Created, rotatedBody);
            using var rotated = JsonDocument.Parse(rotatedBody);
            var rotatedVersion = rotated.RootElement.GetProperty("order").GetProperty("version").GetInt64();

            var trueToFalse = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/payment-links?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { sendNotification = false }),
            };
            AddAdminHeaders(trueToFalse, $"canonical-rotate-{runTag}", $"\"v{issuedVersion}\"");
            using var trueToFalseResponse = await client.SendAsync(trueToFalse);
            Assert.Equal(HttpStatusCode.Conflict, trueToFalseResponse.StatusCode);
            Assert.Contains("idempotency_conflict", await trueToFalseResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var falseToTrue = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/payment-links?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { sendNotification = false }),
            };
            AddAdminHeaders(falseToTrue, $"canonical-rotate-false-{runTag}", $"\"v{rotatedVersion}\"");
            using var falseToTrueResponse = await client.SendAsync(falseToTrue);
            var falseToTrueBody = await falseToTrueResponse.Content.ReadAsStringAsync();
            Assert.True(falseToTrueResponse.StatusCode == HttpStatusCode.Created, falseToTrueBody);
            using var falseToTrueJson = JsonDocument.Parse(falseToTrueBody);
            Assert.True(falseToTrueJson.RootElement.GetProperty("order").GetProperty("version").GetInt64()
                > rotatedVersion);

            var falseToTrueChanged = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/payment-links?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { sendNotification = true }),
            };
            AddAdminHeaders(falseToTrueChanged, $"canonical-rotate-false-{runTag}", $"\"v{rotatedVersion}\"");
            using var falseToTrueChangedResponse = await client.SendAsync(falseToTrueChanged);
            Assert.Equal(HttpStatusCode.Conflict, falseToTrueChangedResponse.StatusCode);
            Assert.Contains("idempotency_conflict", await falseToTrueChangedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var originalDraftReplay = CanonicalCreateRequest(
                merchantId, createKey, includeIssueNow: false,
                metadata: JsonNode.Parse(JsonSerializer.SerializeToNode(draftMetadata)!.ToJsonString())!.AsObject(),
                notify: true, notifyEmail: "draft-issue@example.test");
            using var originalDraftResponse = await client.SendAsync(originalDraftReplay);
            Assert.Equal(HttpStatusCode.Created, originalDraftResponse.StatusCode);
            using var originalDraft = JsonDocument.Parse(await originalDraftResponse.Content.ReadAsStringAsync());
            Assert.Equal("Draft", originalDraft.RootElement.GetProperty("order").GetProperty("orderStatus").GetString());
            Assert.Equal(1, originalDraft.RootElement.GetProperty("order").GetProperty("version").GetInt64());
            Assert.Equal(JsonValueKind.Null, originalDraft.RootElement.GetProperty("paymentLink").ValueKind);

            await using var finalProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(8, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT Status FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT IsFrozen FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT COUNT(*) FROM checkout.PaymentLinks WHERE OrderId=@order AND Status=1;",
                ("@order", orderId))));
            Assert.Equal(2, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT COUNT(*) FROM checkout.PaymentLinks WHERE OrderId=@order AND Status=2;",
                ("@order", orderId))));
            Assert.Equal(2, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant AND Type=N'PaymentLinkNotificationRequestedV1';",
                ("@merchant", merchantId))));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                finalProbe, "SELECT COUNT(*) FROM txn.AdminOperationRecords WHERE MerchantId=@merchant AND Operation=N'order.create';",
                ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.7")]
    [Trait("Requirement", "REQ-6.8")]
    [Trait("Requirement", "REQ-6.9")]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Canonical_default_issue_replays_protected_result_and_rejects_forged_quote_without_writing()
    {
        var database = NewReviewFixDatabaseName();
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        using var factory = new C1SqlFactory(database);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var merchantId = Guid.CreateVersion7();
        factory.Identity.MerchantId = merchantId;
        var runTag = Guid.NewGuid().ToString("N");
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"canonical-replay-{Guid.NewGuid():N}"[..20]);
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
            await SeedIdentityAccessAsync(seed, merchantId, "c1-replay");

        try
        {
            var key = $"canonical-true-{runTag}";
            var orderMetadata = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["data"] = new JsonObject { ["channel"] = "metadata-roundtrip" },
            };
            var itemMetadata = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["data"] = new JsonObject { ["source"] = "client" },
            };
            using var create = CanonicalCreateRequest(
                merchantId, key, includeIssueNow: null, metadata: orderMetadata, itemMetadata: itemMetadata,
                notify: true);
            using var createdResponse = await client.SendAsync(create);
            var createdBody = await createdResponse.Content.ReadAsStringAsync();
            Assert.True(createdResponse.StatusCode == HttpStatusCode.Created, createdBody);
            using var created = JsonDocument.Parse(createdBody);
            var orderId = created.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
            var rawToken = created.RootElement.GetProperty("rawToken").GetString();
            var linkId = created.RootElement.GetProperty("paymentLink").GetProperty("linkId").GetGuid();
            Assert.NotNull(rawToken);
            Assert.Equal(1, created.RootElement.GetProperty("order").GetProperty("metadata")
                .GetProperty("schemaVersion").GetInt32());
            Assert.Equal("metadata-roundtrip", created.RootElement.GetProperty("order")
                .GetProperty("metadata").GetProperty("data").GetProperty("channel").GetString());
            Assert.Equal(1, created.RootElement.GetProperty("order").GetProperty("items")[0]
                .GetProperty("requestMetadata").GetProperty("schemaVersion").GetInt32());

            await using (var bump = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
                await Integration.Tests.IntegrationDb.ExecAsync(bump, """
                    UPDATE acct.Accounts
                    SET AuthorizationVersion = AuthorizationVersion + 1, UpdatedAt = SYSUTCDATETIME()
                    WHERE Id=@account;
                    """, ("@account", Task8A1SqlFactory.AdminId));
            factory.Identity.Account.BumpAuthorizationVersion(DateTime.UtcNow);

            var reorderedOrderMetadata = new JsonObject
            {
                ["data"] = new JsonObject { ["channel"] = "metadata-roundtrip" },
                ["schemaVersion"] = 1,
            };
            var reorderedItemMetadata = new JsonObject
            {
                ["data"] = new JsonObject { ["source"] = "client" },
                ["schemaVersion"] = 1,
            };

            using var replay = CanonicalCreateRequest(
                merchantId, key, includeIssueNow: true, metadata: reorderedOrderMetadata, itemMetadata: reorderedItemMetadata,
                notify: true);
            using var replayResponse = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
            using var replayDocument = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
            Assert.Equal(orderId, replayDocument.RootElement.GetProperty("order").GetProperty("orderId").GetGuid());
            Assert.Equal(rawToken, replayDocument.RootElement.GetProperty("rawToken").GetString());

            using var changed = CanonicalCreateRequest(
                merchantId, key, includeIssueNow: true, productReference: "SKU-CHANGED");
            using var changedResponse = await client.SendAsync(changed);
            Assert.Equal(HttpStatusCode.Conflict, changedResponse.StatusCode);

            using var forged = CanonicalCreateRequest(
                merchantId, $"canonical-forged-{runTag}", includeIssueNow: true,
                unitPrice: "9999.0000", lineAmount: "9999.0000");
            using var forgedResponse = await client.SendAsync(forged);
            Assert.Equal(HttpStatusCode.Conflict, forgedResponse.StatusCode);
            Assert.Contains("pricing_mismatch", await forgedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var adjustment = CanonicalCreateRequest(
                merchantId, $"canonical-adjustment-{runTag}", includeIssueNow: true,
                orderDiscountAmount: "3.0000");
            using var adjustmentResponse = await client.SendAsync(adjustment);
            Assert.Equal(HttpStatusCode.Conflict, adjustmentResponse.StatusCode);
            Assert.Contains("pricing_mismatch", await adjustmentResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var invalidOwner = CanonicalCreateRequest(
                merchantId, $"canonical-owner-{runTag}", includeIssueNow: true,
                ownerSaleId: Guid.CreateVersion7());
            using var invalidOwnerResponse = await client.SendAsync(invalidOwner);
            Assert.Equal(HttpStatusCode.Forbidden, invalidOwnerResponse.StatusCode);

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;",
                ("@merchant", merchantId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM checkout.PaymentLinks WHERE MerchantId=@merchant;",
                ("@merchant", merchantId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM checkout.PaymentLinkReplays WHERE MerchantId=@merchant;",
                ("@merchant", merchantId))));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM txn.AdminOperationRecords WHERE MerchantId=@merchant AND Operation=N'order.create';",
                ("@merchant", merchantId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant AND Id=@link AND Type=N'PaymentLinkNotificationRequestedV1' AND SchemaVersion=N'v1';",
                ("@merchant", merchantId), ("@link", linkId))));
            var outboxPayload = Convert.ToString(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT TOP 1 Payload FROM txn.OutboxMessages WHERE MerchantId=@merchant ORDER BY OccurredAt DESC;",
                ("@merchant", merchantId)));
            Assert.DoesNotContain(rawToken!, outboxPayload, StringComparison.Ordinal);
            Assert.Contains(linkId.ToString("D"), outboxPayload, StringComparison.Ordinal);
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Canonical_omitted_and_explicit_zero_adjustments_fail_against_trusted_nonzero_without_writes()
    {
        var database = NewReviewFixDatabaseName();
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        using var factory = new C1SqlFactory(database, useNonZeroAdjustments: true);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var merchantId = Guid.CreateVersion7();
        factory.Identity.MerchantId = merchantId;
        var runTag = Guid.NewGuid().ToString("N");
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"canonical-adjustment-{runTag}"[..20]);
            await SeedIdentityAccessAsync(seed, merchantId, "c1-adjustment");
        }

        try
        {
            using var omitted = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "insurance",
                    currency = "THB",
                    items = new[]
                    {
                        new
                        {
                            productReference = "SKU-CANONICAL",
                            productCode = "SKU-CANONICAL",
                            productName = "C1 product",
                            quantity = 1,
                            unitPrice = "100.0000",
                            discountAmount = "0.0000",
                            taxAmount = "0.0000",
                            lineAmount = "100.0000",
                        },
                    },
                    issueNow = false,
                }),
            };
            AddAdminHeaders(omitted, $"adjustment-omitted-{runTag}");
            AddIdentityBearer(omitted);
            using var omittedResponse = await client.SendAsync(omitted);
            Assert.Equal(HttpStatusCode.Conflict, omittedResponse.StatusCode);
            Assert.Contains("pricing_mismatch", await omittedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var explicitZero = CanonicalCreateRequest(
                merchantId, $"adjustment-explicit-{runTag}", includeIssueNow: false);
            using var explicitZeroResponse = await client.SendAsync(explicitZero);
            Assert.Equal(HttpStatusCode.Conflict, explicitZeroResponse.StatusCode);
            Assert.Contains("pricing_mismatch", await explicitZeroResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var missingRecipient = CanonicalCreateRequest(
                merchantId, $"adjustment-missing-recipient-{runTag}", includeIssueNow: null);
            using var missingRecipientJson = JsonDocument.Parse(
                await missingRecipient.Content!.ReadAsStringAsync());
            var missingBody = JsonNode.Parse(missingRecipientJson.RootElement.GetRawText())!.AsObject();
            missingBody["notificationIntent"] = new JsonObject { ["send"] = true };
            missingRecipient.Content = JsonContent.Create(missingBody);
            using var missingRecipientResponse = await client.SendAsync(missingRecipient);
            Assert.Equal(HttpStatusCode.BadRequest, missingRecipientResponse.StatusCode);
            Assert.Contains("notification_recipient_required",
                await missingRecipientResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Theory]
    [InlineData("outbox")]
    [InlineData("protector")]
    [Trait("Requirement", "REQ-6.9")]
    public async Task Canonical_notification_failure_rolls_back_order_link_replay_and_outbox(string failure)
    {
        var database = NewReviewFixDatabaseName();
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        using var factory = new C1SqlFactory(
            database,
            throwNotificationOutbox: failure == "outbox",
            throwNotificationProtector: failure == "protector");
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var merchantId = Guid.CreateVersion7();
        factory.Identity.MerchantId = merchantId;
        var runTag = Guid.NewGuid().ToString("N");
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"notification-failure-{runTag}"[..20]);
            await SeedIdentityAccessAsync(seed, merchantId, "c1-notification-failure");
        }

        try
        {
            using var request = CanonicalCreateRequest(
                merchantId, $"notification-failure-{runTag}", includeIssueNow: null, notify: true);
            using var response = await client.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict,
                await response.Content.ReadAsStringAsync());

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM checkout.PaymentLinks WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM checkout.PaymentLinkReplays WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static HttpRequestMessage CanonicalCreateRequest(
        Guid merchantId,
        string key,
        bool? includeIssueNow,
        string productReference = "SKU-CANONICAL",
        string unitPrice = "100.0000",
        string lineAmount = "100.0000",
        string orderDiscountAmount = "0.0000",
        Guid? ownerSaleId = null,
        JsonObject? metadata = null,
        JsonObject? itemMetadata = null,
        bool notify = false,
        string notifyEmail = "notify@example.test",
        string notifyPhone = "+66800000000")
    {
        var body = new JsonObject
        {
            ["businessType"] = "insurance",
            ["currency"] = "THB",
            ["orderDiscountAmount"] = orderDiscountAmount,
            ["orderChargeAmount"] = "0.0000",
            ["items"] = new JsonArray
            {
                new JsonObject
                {
                    ["productReference"] = productReference,
                    ["productCode"] = productReference,
                    ["productName"] = "C1 product",
                    ["quantity"] = 1,
                    ["unitPrice"] = unitPrice,
                    ["discountAmount"] = "0.0000",
                    ["taxAmount"] = "0.0000",
                    ["lineAmount"] = lineAmount,
                },
            },
        };
        if (metadata is not null)
            body["metadata"] = metadata.DeepClone();
        if (itemMetadata is not null)
            ((JsonObject)((JsonArray)body["items"]!)[0]!) ["metadata"] = itemMetadata.DeepClone();
        if (notify)
            body["notificationIntent"] = new JsonObject
            {
                ["send"] = true,
                ["email"] = notifyEmail,
                ["phoneNumber"] = notifyPhone,
            };
        if (includeIssueNow is { } issueNow)
            body["issueNow"] = issueNow;
        if (ownerSaleId is { } owner)
            body["ownerSaleId"] = owner.ToString("D");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
        {
            Content = JsonContent.Create(body),
        };
        AddAdminHeaders(request, key);
        AddIdentityBearer(request);
        return request;
    }

    private static HttpRequestMessage PatchNotificationIntent(
        Guid merchantId,
        Guid orderId,
        long version,
        string key,
        bool? send,
        string? email,
        string? phone,
        JsonObject? metadata = null,
        bool includeCoreFields = false)
    {
        var body = new JsonObject();
        if (includeCoreFields)
        {
            body["businessType"] = "insurance";
            body["ownerSaleId"] = null;
            body["ownerBranchId"] = null;
            body["items"] = new JsonArray
            {
                new JsonObject
                {
                    ["productReference"] = "SKU-CANONICAL",
                    ["quantity"] = 1,
                },
            };
        }
        if (metadata is not null)
            body["metadata"] = metadata.DeepClone();
        if (send is { } intent)
            body["notificationIntent"] = new JsonObject
            {
                ["send"] = intent,
                ["email"] = email,
                ["phoneNumber"] = phone,
            };
        var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("If-Match", $"\"v{version}\"");
        AddIdentityBearer(request);
        return request;
    }

    private static async Task SeedIdentityAccessAsync(
        SqlConnection connection, Guid merchantId, string tag)
    {
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        await Integration.Tests.IntegrationDb.ExecAsync(connection, """
            INSERT acct.Accounts
                (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
            VALUES (@account, 1, N'C1 identity', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
            VALUES (@access, @account, @merchant, 1, 1, 1);
            INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
            VALUES (@role, @code, N'C1 order role', NULL, NULL, 1, 1, 2, @merchant);
            INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
            VALUES (NEWID(), @role, N'payment.create');
            INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
            VALUES (NEWID(), @access, @merchant, @role);
            """,
            ("@account", Task8A1SqlFactory.AdminId), ("@access", accessId),
            ("@merchant", merchantId), ("@role", roleId),
            ("@code", $"{tag}-{Guid.NewGuid():N}"[..24]));
    }

    private static void AddIdentityBearer(HttpRequestMessage request) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "canonical-test-token");

    private static string NewReviewFixDatabaseName() => $"PolPr253C1{Guid.NewGuid():N}";

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

    private static void AddAdminHeadersWithoutIdempotency(HttpRequestMessage request, string? etag = null)
    {
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, "csrf-c1");
        var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{cookieName}=csrf-c1");
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }
}

file sealed class CanonicalIdentityState
{
    public Account Account { get; } = CreateAccount();
    public Guid MerchantId { get; set; }

    private static Account CreateAccount()
    {
        var account = Account.Create(AccountType.Employee, "canonical-identity", DateTime.UtcNow);
        typeof(SharedKernel.Entity<Guid>).GetProperty(nameof(SharedKernel.Entity<Guid>.Id))!
            .SetValue(account, Task8A1SqlFactory.AdminId);
        return account;
    }
}

file sealed class CanonicalIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    CanonicalIdentityState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity("CanonicalIdentity");
        identity.AddClaim(new Claim("sub", state.Account.Id.ToString("D")));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, state.Account.Id.ToString("D")));
        identity.AddClaim(new Claim("authz_version", state.Account.AuthorizationVersion.ToString()));
        identity.AddClaim(new Claim("merchant_id", state.MerchantId.ToString("D")));
        identity.AddClaim(new Claim("token_context", "MERCHANT"));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), "CanonicalIdentity")));
    }
}

file sealed class C1SqlFactory : Task8A1SqlFactory
{
    private readonly string? _database;
    private readonly bool _useNonZeroAdjustments;
    private readonly bool _throwNotificationOutbox;
    private readonly bool _throwNotificationProtector;

    public C1SqlFactory(
        string? database = null,
        bool useNonZeroAdjustments = false,
        bool throwNotificationOutbox = false,
        bool throwNotificationProtector = false)
    {
        _database = database;
        _useNonZeroAdjustments = useNonZeroAdjustments;
        _throwNotificationOutbox = throwNotificationOutbox;
        _throwNotificationProtector = throwNotificationProtector;
    }

    public CanonicalIdentityState Identity { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        if (_database is not null)
        {
            var connection = Integration.Tests.IntegrationDb.AppConnFor(_database);
            builder.UseSetting("ConnectionStrings:App", connection);
            builder.UseSetting("ConnectionStrings:Admin", connection);
            builder.UseSetting("ConnectionStrings:Platform", connection);
        }
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Identity);
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, CanonicalIdentityHandler>("CanonicalIdentity", _ => { });
            services.AddAuthorizationBuilder()
                .AddPolicy("identity-platform", policy => policy
                    .AddAuthenticationSchemes("CanonicalIdentity")
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ApiIdentity.IdentityAccessRequirement()));
            services.PostConfigure<Microsoft.AspNetCore.Authorization.AuthorizationOptions>(options =>
                options.AddPolicy("identity-platform", policy => policy
                    .AddAuthenticationSchemes("CanonicalIdentity")
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ApiIdentity.IdentityAccessRequirement())));
            services.RemoveAll<ITrustedOrderPricingSource>();
            services.AddScoped<ITrustedOrderPricingSource>(_ =>
                _useNonZeroAdjustments ? new C1NonZeroAdjustmentPricing() : new C1TrustedPricing());
            if (_throwNotificationOutbox)
            {
                services.RemoveAll<IOutbox>();
                services.AddScoped<IOutbox, ThrowingNotificationOutbox>();
            }
            if (_throwNotificationProtector)
            {
                services.RemoveAll<IPaymentLinkNotificationProtector>();
                services.AddSingleton<IPaymentLinkNotificationProtector, ThrowingNotificationProtector>();
            }
            services.PostConfigure<PolicySchemeOptions>(
                ApiHost::Api.Iam.ConsoleSessionAuthentication.SchemeName,
                options => options.ForwardDefaultSelector = context =>
                {
                    if (ApiHost::Api.Iam.IdentityPermissionAuthorization.IsIdentityOrderRoute(context)
                        && ApiHost::Api.Iam.IdentityPermissionAuthorization.IsIdentityRequest(context))
                    {
                        context.Features.Set(new ApiHost::Api.Iam.SelectedConsoleAudience(
                            ApiHost::Api.Iam.ConsoleAudience.Merchant));
                        return "CanonicalIdentity";
                    }
                    context.Features.Set(new ApiHost::Api.Iam.SelectedConsoleAudience(
                        ApiHost::Api.Iam.ConsoleAudience.Admin));
                    return Task8A1AdminAuthHandler.SchemeName;
                });
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

file sealed class C1NonZeroAdjustmentPricing : ITrustedOrderPricingSource
{
    public Task<TrustedOrderPricing> PriceAsync(
        Guid merchantId, string businessType, IReadOnlyList<OrderItemRequest> requestedItems,
        CancellationToken cancellationToken)
    {
        var lines = requestedItems.Select(item => new TrustedOrderLineInput(
            item.ProductReference, "card", "C1 product", item.Quantity,
            Money.Of(100m, "THB"), Money.Of(0m, "THB"), Money.Of(0m, "THB"),
            Money.Of(100m * item.Quantity, "THB"), "task8-c1-adjustment-test")).ToArray();
        return Task.FromResult(new TrustedOrderPricing(
            "THB", lines, Money.Of(3m, "THB"), Money.Of(2m, "THB")));
    }
}

file sealed class ThrowingNotificationOutbox : IOutbox
{
    public void Enqueue(Mediator.INotification notification) =>
        throw new InvalidOperationException("forced notification outbox failure");
}

file sealed class ThrowingNotificationProtector : IPaymentLinkNotificationProtector
{
    public string Protect(string rawToken, DateTime expiresAt) =>
        throw new InvalidOperationException("forced notification protector failure");

    public string? Unprotect(string protectedToken) => null;
}
