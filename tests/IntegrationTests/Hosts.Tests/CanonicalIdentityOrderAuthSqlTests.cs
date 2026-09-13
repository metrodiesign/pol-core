extern alias ApiHost;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Accounts.Application;
using Accounts.Domain;
using BuildingBlocks.Application;
using Checkouts.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mediator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orders.Application;
using Orders.Domain;
using Products.Application.Ports;
using Products.Domain;
using SharedKernel;
using ApiIdentity = ApiHost::Api.IdentityAccess;

namespace Hosts.Tests;

[Collection("ApiOperationsSql")]
[Trait("Category", "Integration")]
[Trait("Capability", "ApiOperations")]
public sealed class CanonicalIdentityOrderAuthSqlTests
{
    [Fact]
    [Trait("Requirement", "REQ-3")]
    [Trait("Requirement", "REQ-6.8")]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Employee_identity_platform_account_with_real_sql_access_can_create_a_draft_order()
    {
        var database = $"PolPr253Emp{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"identity-order-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT acct.Accounts
                    (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Order Employee', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess
                    (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 1, 1, 1);
                INSERT iam.Roles
                    (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @code, N'Order writer', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@account", accountId), ("@access", accessId), ("@merchant", merchantId),
                ("@role", roleId), ("@code", $"identity-order-role-{runTag}"[..24]));
        }

        using var factory = new ProductionIdentityOrderFactory(
            new ProductionIdentityOrderAuthState(accountId, merchantId, ProductionActor.Employee, null), database);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "insurance",
                    currency = "THB",
                    items = new[]
                    {
                        new
                        {
                            productReference = "SKU-EMPLOYEE",
                            productCode = "SKU-EMPLOYEE",
                            productName = "Production SQL price",
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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "sql-employee-token");
            request.Headers.Add("Idempotency-Key", $"identity-order-{runTag}");

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, body);

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant AND CreatedByAccountId=@account;
                """, ("@merchant", merchantId), ("@account", accountId))));
            Assert.Equal(7, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify, """
                SELECT Status FROM shop.Orders WHERE MerchantId=@merchant AND CreatedByAccountId=@account;
                """, ("@merchant", merchantId), ("@account", accountId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.8")]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Commerce_authorization_lease_denies_create_after_revoke_without_business_rows()
    {
        var database = $"PolPr253LeaseCreate{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"lease-create-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT acct.Accounts
                    (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Lease Employee', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess
                    (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 1, 1, 1);
                INSERT iam.Roles
                    (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @code, N'Lease writer', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@account", accountId), ("@access", accessId), ("@merchant", merchantId),
                ("@role", roleId), ("@code", $"lease-create-role-{runTag}"[..24]));
        }

        try
        {
            using var factory = new ProductionIdentityOrderFactory(
                new ProductionIdentityOrderAuthState(accountId, merchantId, ProductionActor.Employee, null), database);
            await using (var revoke = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                await Integration.Tests.IntegrationDb.ExecAsync(revoke,
                    "UPDATE acct.Accounts SET AuthorizationVersion=1 WHERE Id=@account;",
                    ("@account", accountId));
            }

            using var scope = factory.Services.CreateScope();
            using var actorBinding = scope.ServiceProvider.GetRequiredService<IActorScope>()
                .Begin(merchantId, accountId);
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            var command = new CreateOrderCommand(
                merchantId,
                accountId,
                "insurance",
                [new OrderItemRequest(
                    "SKU-LEASE",
                    1,
                    ClientSnapshot: new OrderItemClientSnapshot(
                        "SKU-LEASE", "Production SQL price", "100.0000", "0.0000", "0.0000", "100.0000"))],
                new OrderOwnerRequest(null, null),
                IssueNow: false,
                IdempotencyKey: $"lease-revoked-{runTag}",
                Currency: "THB",
                Authorization: new CommerceAuthorizationProof(
                    accountId, 0, merchantId, null, "payment.create", null));

            var denied = await Assert.ThrowsAsync<AccessDeniedException>(
                () => mediator.Send(command).AsTask());
            Assert.Equal("authorization_stale", denied.Code);

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                "SELECT COUNT(*) FROM txn.IdempotencyRecords WHERE MerchantId=@merchant AND Context=N'order.create';",
                ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-3.3")]
    [Trait("Requirement", "REQ-6.4")]
    public async Task Employee_branch_scope_rejects_another_branch_owner_and_allows_its_single_branch()
    {
        var database = $"PolPr253Branch{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var branchA = Guid.CreateVersion7();
        var branchB = Guid.CreateVersion7();
        var saleA = Guid.CreateVersion7();
        var saleB = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"identity-branch-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@branchA, @merchant, @branchACode, N'Branch A', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1),
                       (@branchB, @merchant, @branchBCode, N'Branch B', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@saleA, @merchant, @branchA, @saleACode, N'Sale A', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1),
                       (@saleB, @merchant, @branchB, @saleBCode, N'Sale B', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT acct.Accounts
                    (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Branch Employee', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 3, 1, 1);
                INSERT access.BranchAccess (Id, MerchantAccessId, MerchantId, BranchId)
                VALUES (NEWID(), @access, @merchant, @branchA);
                INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @roleCode, N'Branch Order Role', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create'),
                       (NEWID(), @role, N'payment.view');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@branchA", branchA), ("@branchB", branchB), ("@merchant", merchantId),
                ("@branchACode", $"branch-a-{runTag}"[..20]), ("@branchBCode", $"branch-b-{runTag}"[..20]),
                ("@saleA", saleA), ("@saleB", saleB), ("@saleACode", $"sale-a-{runTag}"[..20]),
                ("@saleBCode", $"sale-b-{runTag}"[..20]), ("@account", accountId), ("@access", accessId),
                ("@role", roleId), ("@roleCode", $"branch-order-role-{runTag}"[..24]));
        }

        try
        {
            using var factory = new ProductionIdentityOrderFactory(
                new ProductionIdentityOrderAuthState(accountId, merchantId, ProductionActor.Employee, null), database);
            using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            using var outside = CanonicalRequest(
                merchantId, $"branch-outside-{runTag}", ownerSaleId: saleB, ownerBranchId: branchB);
            outside.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "employee-token");
            using var outsideResponse = await client.SendAsync(outside);
            Assert.Equal(HttpStatusCode.Forbidden, outsideResponse.StatusCode);
            Assert.Contains("owner_scope_denied", await outsideResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var inside = CanonicalRequest(
                merchantId, $"branch-inside-{runTag}", ownerSaleId: saleA, ownerBranchId: branchA);
            inside.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "employee-token");
            using var insideResponse = await client.SendAsync(inside);
            Assert.True(insideResponse.StatusCode == HttpStatusCode.Created,
                await insideResponse.Content.ReadAsStringAsync());

            var otherOrderId = Guid.CreateVersion7();
            var otherOrderNo = $"BR{otherOrderId:N}"[..12];
            var now = DateTime.UtcNow;
            await using (var seedOther = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                await Integration.Tests.IntegrationDb.ExecAsync(seedOther, """
                    INSERT shop.Orders
                        (Id, MerchantId, CreatedByAccountId, OwnerSaleId, OwnerBranchIdAtCreation,
                         OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                         IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                         CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                         SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                         OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
                    VALUES (@order, @merchant, @account, @sale, @branch,
                            @orderNo, 7, 1, @at, @at, 1,
                            0, NULL, NULL, NULL, NULL,
                            N'Other branch customer', N'0800000001', 100.00, 'THB',
                            100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
                    """,
                    ("@order", otherOrderId), ("@merchant", merchantId), ("@account", accountId),
                    ("@sale", saleB), ("@branch", branchB), ("@orderNo", otherOrderNo), ("@at", now));
            }

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            // The canonical order number is server-generated; resolve the in-scope row by its owner tuple.
            var insideOrderId = (Guid)(await Integration.Tests.IntegrationDb.ScalarAsync(verify, """
                SELECT Id FROM shop.Orders
                WHERE MerchantId=@merchant AND OwnerSaleId=@sale AND OwnerBranchIdAtCreation=@branch;
                """, ("@merchant", merchantId), ("@sale", saleA), ("@branch", branchA)) ??
                throw new InvalidOperationException("The in-scope branch order was not found."));

            using var bffScope = factory.Services.CreateScope();
            var httpAccessor = bffScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            httpAccessor.HttpContext = new DefaultHttpContext();
            var identities = bffScope.ServiceProvider.GetRequiredService<IIdentityAccessQuery>();
            var account = await identities.FindAccountAsync(accountId, default)
                ?? throw new InvalidOperationException("The branch BFF account was not found.");
            var bff = bffScope.ServiceProvider.GetRequiredService<ApiIdentity.BffSessionManager>();
            var bffIssue = await bff.CreateAsync(account, null, null, null, merchantId, "/api/v1/orders", default);
            httpAccessor.HttpContext = null;
            var sessionCookie = ApiIdentity.BffSessionManager.SessionCookieNameDevHttp;
            var csrfCookie = ApiIdentity.BffSessionManager.CsrfCookieName;
            var bffCookies = $"{sessionCookie}={bffIssue.SessionToken}; {csrfCookie}={bffIssue.CsrfToken}";

            using var list = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders?merchantId={merchantId:D}");
            list.Headers.Add("Cookie", bffCookies);
            using var listResponse = await client.SendAsync(list);
            var listBody = await listResponse.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
            using var listJson = System.Text.Json.JsonDocument.Parse(listBody);
            var listedIds = listJson.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(x => x.GetProperty("orderId").GetGuid())
                .ToHashSet();
            Assert.Contains(insideOrderId, listedIds);
            Assert.DoesNotContain(otherOrderId, listedIds);

            using var outsidePatch = PatchRequest(
                merchantId, otherOrderId, saleB, branchB, $"branch-patch-outside-{runTag}");
            outsidePatch.Headers.Add("Cookie", bffCookies);
            outsidePatch.Headers.Add(ApiIdentity.BffSessionManager.HeaderName, bffIssue.CsrfToken);
            outsidePatch.Headers.Add("If-Match", "\"v1\"");
            using var outsidePatchResponse = await client.SendAsync(outsidePatch);
            Assert.Equal(HttpStatusCode.NotFound, outsidePatchResponse.StatusCode);

            var insideVersion = Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", insideOrderId)));
            using var ownPatch = PatchRequest(
                merchantId, insideOrderId, saleA, branchA, $"branch-patch-own-{runTag}");
            ownPatch.Headers.Add("Cookie", bffCookies);
            ownPatch.Headers.Add(ApiIdentity.BffSessionManager.HeaderName, bffIssue.CsrfToken);
            ownPatch.Headers.Add("If-Match", $"\"v{insideVersion}\"");
            using var ownPatchResponse = await client.SendAsync(ownPatch);
            Assert.True(ownPatchResponse.StatusCode == HttpStatusCode.OK,
                await ownPatchResponse.Content.ReadAsStringAsync());

            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant AND OwnerSaleId=@sale AND OwnerBranchIdAtCreation=@branch;
                """, ("@merchant", merchantId), ("@sale", saleA), ("@branch", branchA))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify, """
                SELECT Version FROM shop.Orders WHERE Id=@order;
                """, ("@order", otherOrderId))));
            Assert.Equal(2, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;
                """, ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-6.5")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Restricted_identity_owner_omission_fails_before_write_but_explicit_owner_uses_production_source()
    {
        var database = $"PolPr253OwnerOmit{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var branchId = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var gateway = new OwnerDocumentGateway(new SpDocumentItem(
            "Motor", "CMI", "POLICY", "DOC-OWNER", "2026", "BKK", "REF", "1", "2026", "1",
            "BKK", "AUTO", "SALE-OWNER", "Owner sale", null, null, "POL-OWNER", "APP-OWNER", null, null,
            DateTime.UtcNow, DateTime.UtcNow.AddYears(1), "Owner policy", 100m, 5m, 20m, 125m, 10m,
            10m, DateTime.UtcNow, "1กก1234", "UNPAID"));

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(
                seed, merchantId, $"owner-omit-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@branch, @merchant, N'owner-branch', N'Owner branch', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@sale, @merchant, @branch, N'SALE-OWNER', N'Owner sale', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT acct.Accounts
                    (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Assigned owner', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 4, 1, 1);
                INSERT access.BranchAccess (Id, MerchantAccessId, MerchantId, BranchId)
                VALUES (NEWID(), @access, @merchant, @branch);
                INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @code, N'Owner writer', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@branch", branchId), ("@merchant", merchantId), ("@sale", saleId),
                ("@account", accountId), ("@access", accessId), ("@role", roleId),
                ("@code", $"owner-omit-role-{runTag}"[..24]));
        }

        using var factory = new ProductionIdentityOrderFactory(
            new ProductionIdentityOrderAuthState(accountId, merchantId, ProductionActor.Employee, null),
            database, useProductionPricing: true, gateway: gateway);
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        try
        {
            using var omitted = OwnerRequest(merchantId, accountId, $"owner-omit-{runTag}", null, null);
            using var omittedResponse = await client.SendAsync(omitted);
            Assert.Equal(HttpStatusCode.Conflict, omittedResponse.StatusCode);
            Assert.Contains("owner_required", await omittedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var explicitOwner = OwnerRequest(
                merchantId, accountId, $"owner-explicit-{runTag}", saleId, branchId);
            using var explicitResponse = await client.SendAsync(explicitOwner);
            Assert.True(explicitResponse.StatusCode == HttpStatusCode.Created,
                await explicitResponse.Content.ReadAsStringAsync());

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                "SELECT COUNT(*) FROM txn.IdempotencyRecords WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static HttpRequestMessage OwnerRequest(
        Guid merchantId, Guid accountId, string key, Guid? ownerSaleId, Guid? ownerBranchId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
        {
            Content = JsonContent.Create(new
            {
                businessType = "insurance",
                currency = "THB",
                issueNow = false,
                ownerSaleId,
                ownerBranchId,
                items = new[]
                {
                    new
                    {
                        productReference = "DOC-OWNER",
                        productCode = "DOC-OWNER",
                        productName = "Owner policy",
                        quantity = 1,
                        unitPrice = "125.0000",
                        discountAmount = "0.0000",
                        taxAmount = "0.0000",
                        lineAmount = "125.0000",
                    },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "owner-token");
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Fact]
    [Trait("Requirement", "REQ-3")]
    [Trait("Requirement", "REQ-6.3")]
    [Trait("Requirement", "REQ-6.8")]
    public async Task Agent_and_system_identity_platform_accounts_create_with_trusted_scope_and_owner_rules()
    {
        var database = $"PolPr253Agent{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var otherMerchantId = Guid.CreateVersion7();
        var branchId = Guid.CreateVersion7();
        var saleId = Guid.CreateVersion7();
        var agentAccountId = Guid.CreateVersion7();
        var systemAccountId = Guid.CreateVersion7();
        var systemClientId = Guid.CreateVersion7();
        var agentAccessId = Guid.CreateVersion7();
        var systemAccessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var clientCode = $"system-order-{runTag}"[..Math.Min(128, $"system-order-{runTag}".Length)];

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantId, $"agent-order-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, otherMerchantId, $"other-order-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@branch, @merchant, @branchCode, N'Agent Branch', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT merch.Sales (Id, MerchantId, BranchId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@sale, @merchant, @branch, @saleCode, N'Agent Sale', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@agent, 2, N'Order Agent', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@system, 3, N'Order System', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT acct.Agents (AccountId, MerchantId, SaleId, Metadata, Id)
                VALUES (@agent, @merchant, @sale, N'{}', @agent);
                INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@agentAccess, @agent, @merchant, 1, 1, 1),
                       (@systemAccess, @system, @merchant, 1, 1, 1);
                INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @roleCode, N'Agent Order Role', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @agentAccess, @merchant, @role);
                INSERT acct.SystemClients
                    (Id, AccountId, ClientId, MerchantId, Environment, Status, AllowedGrantTypes, CreatedAt, UpdatedAt)
                VALUES (@systemClient, @system, @clientCode, @merchant, N'SANDBOX', 1, N'client_credentials', SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.SystemClientScopes (Id, SystemClientId, ScopeCode)
                VALUES (NEWID(), @systemClient, N'order.write');
                """,
                ("@branch", branchId), ("@merchant", merchantId), ("@branchCode", $"branch-{runTag}"[..20]),
                ("@sale", saleId), ("@saleCode", $"sale-{runTag}"[..20]), ("@agent", agentAccountId),
                ("@system", systemAccountId), ("@agentAccess", agentAccessId), ("@systemAccess", systemAccessId),
                ("@role", roleId), ("@roleCode", $"agent-order-role-{runTag}"[..24]),
                ("@systemClient", systemClientId), ("@clientCode", clientCode));
        }

        try
        {
            using var factory = new ProductionIdentityOrderFactory(
                new ProductionIdentityOrderAuthState(agentAccountId, merchantId, ProductionActor.Agent, null), database);
            using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });

            using var agentRequest = CanonicalRequest(merchantId, $"agent-order-{runTag}");
            agentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");
            using var agentResponse = await client.SendAsync(agentRequest);
            Assert.True(agentResponse.StatusCode == HttpStatusCode.Created,
                await agentResponse.Content.ReadAsStringAsync());

            factory.State.Actor = ProductionActor.System;
            factory.State.AccountId = systemAccountId;
            factory.State.ClientId = clientCode;
            factory.State.ScopeGranted = true;
            using var systemRequest = CanonicalRequest(merchantId, $"system-order-{runTag}");
            systemRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "system-token");
            using var systemResponse = await client.SendAsync(systemRequest);
            Assert.Equal(HttpStatusCode.Created, systemResponse.StatusCode);

            using var wrongMerchant = CanonicalRequest(otherMerchantId, $"wrong-merchant-{runTag}");
            wrongMerchant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");
            factory.State.Actor = ProductionActor.Agent;
            factory.State.AccountId = agentAccountId;
            factory.State.ClientId = null;
            using var wrongMerchantResponse = await client.SendAsync(wrongMerchant);
            Assert.Equal(HttpStatusCode.Forbidden, wrongMerchantResponse.StatusCode);

            factory.State.EmitMerchantClaim = false;
            using var missingMerchant = CanonicalRequest(merchantId, $"missing-merchant-{runTag}");
            missingMerchant.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");
            using var missingMerchantResponse = await client.SendAsync(missingMerchant);
            Assert.Equal(HttpStatusCode.Forbidden, missingMerchantResponse.StatusCode);
            Assert.Contains("merchant_context_missing", await missingMerchantResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            factory.State.EmitMerchantClaim = true;

            using var conflictingOwner = CanonicalRequest(
                merchantId, $"conflicting-owner-{runTag}", ownerSaleId: Guid.CreateVersion7());
            conflictingOwner.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");
            using var conflictingOwnerResponse = await client.SendAsync(conflictingOwner);
            Assert.Equal(HttpStatusCode.Forbidden, conflictingOwnerResponse.StatusCode);
            Assert.Contains("owner_sale_conflict", await conflictingOwnerResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await using (var revoke = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
                await Integration.Tests.IntegrationDb.ExecAsync(revoke,
                    "DELETE FROM iam.RolePermissions WHERE RoleId=@role; DELETE FROM access.SystemClientScopes WHERE SystemClientId=@client;",
                    ("@role", roleId), ("@client", systemClientId));

            factory.State.Actor = ProductionActor.Agent;
            factory.State.AccountId = agentAccountId;
            factory.State.ClientId = null;
            using var missingPermission = CanonicalRequest(merchantId, $"missing-permission-{runTag}");
            missingPermission.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "agent-token");
            using var missingPermissionResponse = await client.SendAsync(missingPermission);
            Assert.Equal(HttpStatusCode.Forbidden, missingPermissionResponse.StatusCode);

            factory.State.Actor = ProductionActor.System;
            factory.State.AccountId = systemAccountId;
            factory.State.ClientId = clientCode;
            factory.State.ScopeGranted = false;
            using var missingScope = CanonicalRequest(merchantId, $"missing-scope-{runTag}");
            missingScope.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "system-token");
            using var missingScopeResponse = await client.SendAsync(missingScope);
            Assert.Equal(HttpStatusCode.Forbidden, missingScopeResponse.StatusCode);

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(2, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                "SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
            Assert.Equal(1, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(verify, """
                SELECT COUNT(*) FROM shop.Orders WHERE MerchantId=@merchant AND OwnerSaleId=@sale;
                """, ("@merchant", merchantId), ("@sale", saleId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static HttpRequestMessage CanonicalRequest(
        Guid merchantId, string key, Guid? ownerSaleId = null, Guid? ownerBranchId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
        {
            Content = JsonContent.Create(new
        {
            businessType = "insurance",
            currency = "THB",
            issueNow = false,
            ownerSaleId,
            ownerBranchId,
            items = new[]
            {
                new
                {
                    productReference = "SKU-AUTH",
                    productCode = "SKU-AUTH",
                    productName = "Production SQL price",
                    quantity = 1,
                    unitPrice = "100.0000",
                    discountAmount = "0.0000",
                    taxAmount = "0.0000",
                    lineAmount = "100.0000",
                },
            },
        }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static HttpRequestMessage PatchRequest(
        Guid merchantId, Guid orderId, Guid? ownerSaleId, Guid? ownerBranchId, string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Patch, $"/api/v1/orders/{orderId:D}?merchantId={merchantId:D}")
        {
            Content = JsonContent.Create(new
            {
                businessType = "insurance",
                ownerSaleId,
                ownerBranchId,
                items = new[] { new { productReference = "SKU-AUTH", quantity = 1 } },
            }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }
}

file enum ProductionActor { Employee, Agent, System }

file sealed class ProductionIdentityOrderAuthState(
    Guid accountId, Guid merchantId, ProductionActor actor, string? clientId)
{
    public Guid AccountId { get; set; } = accountId;
    public Guid MerchantId { get; } = merchantId;
    public ProductionActor Actor { get; set; } = actor;
    public string? ClientId { get; set; } = clientId;
    public bool ScopeGranted { get; set; } = true;
    public bool EmitMerchantClaim { get; set; } = true;
}

file sealed class ProductionIdentityOrderAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    System.Text.Encodings.Web.UrlEncoder encoder,
    ProductionIdentityOrderAuthState state)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity("ProductionIdentityOrderAuth");
        identity.AddClaim(new Claim("sub", state.AccountId.ToString("D")));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, state.AccountId.ToString("D")));
        identity.AddClaim(new Claim("authz_version", "0"));
        if (state.EmitMerchantClaim)
            identity.AddClaim(new Claim("merchant_id", state.MerchantId.ToString("D")));
        identity.AddClaim(new Claim("token_context", "MERCHANT"));
        if (state.ClientId is not null)
            identity.AddClaim(new Claim("client_id", state.ClientId));
        if (state.Actor == ProductionActor.System && state.ScopeGranted)
            identity.AddClaim(new Claim("scope", "order.write"));
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), "ProductionIdentityOrderAuth")));
    }
}

file sealed class ProductionIdentityOrderFactory(
    ProductionIdentityOrderAuthState state,
    string database,
    bool useProductionPricing = false,
    ISpDocumentGateway? gateway = null) : Task8A1SqlFactory
{
    public ProductionIdentityOrderAuthState State => state;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var connection = Integration.Tests.IntegrationDb.AppConnFor(database);
        builder.UseSetting("ConnectionStrings:App", connection);
        builder.UseSetting("ConnectionStrings:Admin", connection);
        builder.UseSetting("ConnectionStrings:Platform", connection);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(state);
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, ProductionIdentityOrderAuthHandler>(
                    "ProductionIdentityOrderAuth", _ => { });
            services.AddAuthorizationBuilder()
                .AddPolicy("identity-platform", policy => policy
                    .AddAuthenticationSchemes("ProductionIdentityOrderAuth")
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ApiIdentity.IdentityAccessRequirement()));
            if (!useProductionPricing)
            {
                services.RemoveAll<ITrustedOrderPricingSource>();
                services.AddScoped<ITrustedOrderPricingSource, ProductionIdentityOrderPricing>();
            }
            if (gateway is not null)
            {
                services.RemoveAll<ISpDocumentGateway>();
                services.AddSingleton(gateway);
            }
        });
    }
}

file sealed class ProductionIdentityOrderPricing : ITrustedOrderPricingSource
{
    public Task<TrustedOrderPricing> PriceAsync(
        Guid merchantId, string businessType, IReadOnlyList<OrderItemRequest> requestedItems,
        CancellationToken cancellationToken)
    {
        var lines = requestedItems.Select(item => new TrustedOrderLineInput(
            item.ProductReference, "card", "Production SQL price", item.Quantity,
            Money.Of(100m, "THB"), Money.Zero("THB"), Money.Zero("THB"),
            Money.Of(100m * item.Quantity, "THB"), "production-sql-test")).ToArray();
        return Task.FromResult(new TrustedOrderPricing(
            "THB", lines, Money.Zero("THB"), Money.Zero("THB")));
    }
}

file sealed class OwnerDocumentGateway(SpDocumentItem document) : ISpDocumentGateway
{
    public Task<SpDocumentSearchResult> SearchAsync(
        SpDocumentSearchRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new SpDocumentSearchResult(
            new SpPaginationMetadata(1, 1, 1, 25, false, false, "EXACT", 6), [document]));

    public Task<SpDocumentItem?> LookupAsync(
        SpDocumentLookupRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<SpDocumentItem?>(request.ProductGroup == ProductGroup.CMI ? document : null);
}
