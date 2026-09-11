extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Admins.Application;
using Admins.Application.Users;
using Admins.Domain.Users;
using Accounts.Domain;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orders.Application;
using Orders.Domain;
using SharedKernel;

namespace Hosts.Tests;

[Collection("ApiOperationsSql")]
[Trait("Category", "Integration")]
[Trait("Capability", "ApiOperations")]
public sealed class CanonicalBffOrderAuthSqlTests
{
    [Fact]
    [Trait("Requirement", "REQ-3")]
    public async Task Production_bff_session_requires_matching_csrf_for_canonical_order_create()
    {
        var database = $"PolPr253Bff{Guid.NewGuid():N}";
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
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantId, $"bff-order-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Bff Employee', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 1, 1, 1);
                INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @roleCode, N'Bff Order Role', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@account", accountId), ("@access", accessId), ("@merchant", merchantId),
                ("@role", roleId), ("@roleCode", $"bff-order-role-{runTag}"[..24]));
        }

        try
        {
            using var factory = new BffCanonicalFactory(database);
            using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
            using var sessionScope = factory.Services.CreateScope();
            var httpAccessor = sessionScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            httpAccessor.HttpContext = new DefaultHttpContext();
            var identities = sessionScope.ServiceProvider.GetRequiredService<Accounts.Application.IIdentityAccessQuery>();
            var account = await identities.FindAccountAsync(accountId, default)
                ?? throw new InvalidOperationException("Seeded BFF account was not found.");
            var bff = sessionScope.ServiceProvider.GetRequiredService<ApiHost::Api.IdentityAccess.BffSessionManager>();
            var issue = await bff.CreateAsync(account, null, null, null, merchantId, "/api/v1/orders", default);
            httpAccessor.HttpContext = null;

            using var missing = Request(merchantId, issue.SessionToken, issue.CsrfToken, null);
            using var missingResponse = await client.SendAsync(missing);
            Assert.Equal(HttpStatusCode.Forbidden, missingResponse.StatusCode);

            using var invalid = Request(merchantId, issue.SessionToken, issue.CsrfToken, "wrong");
            using var invalidResponse = await client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.Forbidden, invalidResponse.StatusCode);

            using var valid = Request(merchantId, issue.SessionToken, issue.CsrfToken, issue.CsrfToken);
            using var validResponse = await client.SendAsync(valid);
            var validBody = await validResponse.Content.ReadAsStringAsync();
            Assert.True(validResponse.StatusCode == HttpStatusCode.Created, validBody);
            using var validJson = JsonDocument.Parse(validBody);
            var orderId = validJson.RootElement.GetProperty("order").GetProperty("orderId").GetGuid();
            var orderVersion = validJson.RootElement.GetProperty("order").GetProperty("version").GetInt64();

            var sessionCookie = ApiHost::Api.IdentityAccess.BffSessionManager.SessionCookieNameDevHttp;
            var csrfCookie = ApiHost::Api.IdentityAccess.BffSessionManager.CsrfCookieName;
            var cookies = $"{sessionCookie}={issue.SessionToken}; {csrfCookie}={issue.CsrfToken}";
            using var list = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders?merchantId={merchantId:D}");
            list.Headers.Add("Cookie", cookies);
            using var listResponse = await client.SendAsync(list);
            Assert.Equal(HttpStatusCode.Forbidden, listResponse.StatusCode);

            await using var before = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            var beforeSummary = await Integration.Tests.IntegrationDb.ScalarAsync(before,
                "SELECT SummaryToken FROM shop.Orders WHERE Id=@order;", ("@order", orderId));
            var beforeOutbox = Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(before,
                "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant;", ("@merchant", merchantId)));

            using var legacy = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/orders/{orderId:D}/summary/resend?merchantId={merchantId:D}")
            {
                Content = JsonContent.Create(new { }),
            };
            legacy.Headers.Add("Cookie", cookies);
            legacy.Headers.Add("If-Match", $"\"v{orderVersion}\"");
            legacy.Headers.Add("Idempotency-Key", "bff-legacy-resend");
            using var legacyResponse = await client.SendAsync(legacy);
            Assert.True(legacyResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                await legacyResponse.Content.ReadAsStringAsync());

            await using var after = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(orderVersion, Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(after,
                "SELECT Version FROM shop.Orders WHERE Id=@order;", ("@order", orderId))));
            Assert.Equal(beforeSummary?.ToString(), (await Integration.Tests.IntegrationDb.ScalarAsync(after,
                "SELECT SummaryToken FROM shop.Orders WHERE Id=@order;", ("@order", orderId)))?.ToString());
            Assert.Equal(beforeOutbox, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(after,
                "SELECT COUNT(*) FROM txn.OutboxMessages WHERE MerchantId=@merchant;", ("@merchant", merchantId))));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-4.6")]
    [Trait("Requirement", "REQ-10.2")]
    public async Task Role_deactivation_bumps_assigned_account_and_stales_old_bff_before_new_read_is_forbidden()
    {
        var database = $"PolPr253Role{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantId = Guid.CreateVersion7();
        var accountId = Guid.CreateVersion7();
        var unrelatedAccountId = Guid.CreateVersion7();
        var accessId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var roleCode = $"bff-role-{runTag}"[..24];

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantId, $"role-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@account, 1, N'Role BFF Account', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@unrelated, 1, N'Unrelated Account', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@access, @account, @merchant, 1, 1, 1);
                INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@role, @code, N'BFF role', NULL, NULL, 1, 1, 2, @merchant);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @role, N'payment.create'),
                       (NEWID(), @role, N'payment.view');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @access, @merchant, @role);
                """,
                ("@account", accountId), ("@unrelated", unrelatedAccountId), ("@access", accessId),
                ("@merchant", merchantId), ("@role", roleId), ("@code", roleCode));
        }

        try
        {
            using var factory = new BffCanonicalFactory(database);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            using var sessionScope = factory.Services.CreateScope();
            var httpAccessor = sessionScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            httpAccessor.HttpContext = new DefaultHttpContext();
            var identities = sessionScope.ServiceProvider.GetRequiredService<Accounts.Application.IIdentityAccessQuery>();
            var account = await identities.FindAccountAsync(accountId, default)
                ?? throw new InvalidOperationException("The seeded role account was not found.");
            var bff = sessionScope.ServiceProvider.GetRequiredService<ApiHost::Api.IdentityAccess.BffSessionManager>();
            var oldSession = await bff.CreateAsync(account, null, null, null, merchantId, "/api/v1/orders", default);
            httpAccessor.HttpContext = null;

            using var before = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders?merchantId={merchantId:D}");
            before.Headers.Add("Cookie", Cookies(oldSession));
            using var beforeResponse = await client.SendAsync(before);
            Assert.Equal(HttpStatusCode.OK, beforeResponse.StatusCode);

            using var roleRead = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/merchants/{merchantId:D}/roles/{roleCode}");
            AddAdminHeaders(roleRead, $"role-read-{runTag}");
            using var roleReadResponse = await client.SendAsync(roleRead);
            Assert.Equal(HttpStatusCode.OK, roleReadResponse.StatusCode);
            var roleEtag = roleReadResponse.Headers.ETag?.Tag
                ?? throw new InvalidOperationException("Role response did not include an ETag.");

            using var deactivate = new HttpRequestMessage(
                HttpMethod.Put, $"/api/v1/merchants/{merchantId:D}/roles/{roleCode}")
            {
                Content = JsonContent.Create(new
                {
                    name = "BFF role inactive",
                    status = "Inactive",
                    permissions = Array.Empty<string>(),
                }),
            };
            AddAdminHeaders(deactivate, $"role-deactivate-{runTag}", roleEtag);
            using var deactivated = await client.SendAsync(deactivate);
            Assert.True(deactivated.StatusCode == HttpStatusCode.OK,
                await deactivated.Content.ReadAsStringAsync());

            await using (var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(1, Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                    "SELECT AuthorizationVersion FROM acct.Accounts WHERE Id=@account;", ("@account", accountId))));
                Assert.Equal(0, Convert.ToInt64(await Integration.Tests.IntegrationDb.ScalarAsync(verify,
                    "SELECT AuthorizationVersion FROM acct.Accounts WHERE Id=@account;", ("@account", unrelatedAccountId))));
            }

            using var stale = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders?merchantId={merchantId:D}");
            stale.Headers.Add("Cookie", Cookies(oldSession));
            using var staleResponse = await client.SendAsync(stale);
            Assert.Equal(HttpStatusCode.Unauthorized, staleResponse.StatusCode);

            using var freshScope = factory.Services.CreateScope();
            var freshAccessor = freshScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            freshAccessor.HttpContext = new DefaultHttpContext();
            var freshIdentity = freshScope.ServiceProvider.GetRequiredService<Accounts.Application.IIdentityAccessQuery>();
            var freshAccount = await freshIdentity.FindAccountAsync(accountId, default)
                ?? throw new InvalidOperationException("The updated role account was not found.");
            var freshBff = freshScope.ServiceProvider.GetRequiredService<ApiHost::Api.IdentityAccess.BffSessionManager>();
            var freshSession = await freshBff.CreateAsync(freshAccount, null, null, null, merchantId, "/api/v1/orders", default);
            freshAccessor.HttpContext = null;

            using var denied = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders?merchantId={merchantId:D}");
            denied.Headers.Add("Cookie", Cookies(freshSession));
            using var deniedResponse = await client.SendAsync(denied);
            Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-3.6")]
    [Trait("Requirement", "REQ-7.4")]
    public async Task Sequential_identity_console_and_second_tenant_reads_do_not_reuse_order_filter_state()
    {
        var database = $"PolPr253Filter{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        var accountA = Guid.CreateVersion7();
        var accountB = Guid.CreateVersion7();
        var accountC = Guid.CreateVersion7();
        var legacyUserId = Guid.CreateVersion7();
        var legacyOtherUserId = Guid.CreateVersion7();
        var accessA = Guid.CreateVersion7();
        var accessB = Guid.CreateVersion7();
        var roleA = Guid.CreateVersion7();
        var roleB = Guid.CreateVersion7();
        var orderA = Guid.CreateVersion7();
        var orderB = Guid.CreateVersion7();
        var orderC = Guid.CreateVersion7();
        var legacyOrder = Guid.CreateVersion7();
        var legacyOtherOrder = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var legacyToken = $"legacy-{Guid.NewGuid():N}";
        var legacyTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(legacyToken));
        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConnFor(database)))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantA, $"filter-a-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantB, $"filter-b-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@accountA, 1, N'Filter A', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@accountB, 1, N'Filter B', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@accountC, 1, N'Filter C', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT access.MerchantAccess (Id, AccountId, MerchantId, DataScope, Status, Version)
                VALUES (@accessA, @accountA, @merchantA, 1, 1, 1),
                       (@accessB, @accountB, @merchantB, 1, 1, 1);
                INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Version, Scope, MerchantId)
                VALUES (@roleA, @codeA, N'Filter A role', NULL, NULL, 1, 1, 2, @merchantA),
                       (@roleB, @codeB, N'Filter B role', NULL, NULL, 1, 1, 2, @merchantB);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @roleA, N'payment.view'),
                       (NEWID(), @roleA, N'payment.create'),
                       (NEWID(), @roleB, N'payment.view');
                INSERT access.AccessRoles (Id, MerchantAccessId, MerchantId, RoleId)
                VALUES (NEWID(), @accessA, @merchantA, @roleA),
                       (NEWID(), @accessB, @merchantB, @roleB);
                INSERT merch.Users
                    (Id, Provider, Subject, Email, Status, MerchantId, Version, CreatedAt,
                     DisplayName, FirstName, LastName, IdentityType)
                VALUES (@legacyUser, N'google', @legacySubject, N'legacy@example.test', 2, @merchantA, 1,
                        SYSUTCDATETIME(), N'Legacy User', N'Legacy', N'User', 1),
                       (@legacyOther, N'google', @legacyOtherSubject, N'legacy-other@example.test', 2, @merchantA, 1,
                        SYSUTCDATETIME(), N'Legacy Other', N'Legacy', N'Other', 1);
                INSERT merch.RoleAssignments
                    (Id, UserId, RoleId, MerchantId, AssignedById, AssignedAt)
                VALUES (NEWID(), @legacyUser, @roleA, @merchantA, @legacyUser, SYSUTCDATETIME()),
                       (NEWID(), @legacyOther, @roleA, @merchantA, @legacyOther, SYSUTCDATETIME());
                INSERT merch.Sessions
                    (Id, FamilyId, TokenHash, UserId, Status, IssuedAt, IdleExpiresAt, AbsoluteExpiresAt)
                VALUES (NEWID(), NEWID(), @legacyTokenHash, @legacyUser, 1,
                        SYSUTCDATETIME(), DATEADD(hour, 1, SYSUTCDATETIME()), DATEADD(day, 1, SYSUTCDATETIME()));
                INSERT shop.Orders
                    (Id, MerchantId, CreatedByAccountId, OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                     IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                     CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                     SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                     OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
                VALUES (@orderA, @merchantA, @accountA, @orderNoA, 7, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1,
                        0, NULL, NULL, NULL, NULL, N'Filter A', N'0800000000', 100.00, 'THB',
                        100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance'),
                       (@orderB, @merchantB, @accountB, @orderNoB, 7, 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1,
                        0, NULL, NULL, NULL, NULL, N'Filter B', N'0800000001', 100.00, 'THB',
                        100.00, 'THB', 0.00, 'THB', 0.00, 'THB', 'card', N'insurance');
                INSERT shop.Orders
                    (Id, MerchantId, CreatedByAccountId, InitiatingAudience, InitiatingMerchantUserId,
                     OrderNo, Status, PaymentStatus, CreatedAt, UpdatedAt, Version,
                     IsFrozen, IssuedAt, FrozenAt, SummaryToken, SummaryTokenExpiresAt,
                     CustomerName, CustomerPhone, AmountAmount, AmountCurrency,
                     SubtotalAmount, SubtotalCurrency, OrderDiscountAmount, OrderDiscountCurrency,
                     OrderChargeAmount, OrderChargeCurrency, PaymentChannel, BusinessType)
                VALUES (@orderC, @merchantA, @accountC, NULL, NULL, @orderNoC, 7, 1,
                        SYSUTCDATETIME(), SYSUTCDATETIME(), 1, 0, NULL, NULL, NULL, NULL,
                        N'Filter C', N'0800000002', 100.00, 'THB', 100.00, 'THB', 0.00, 'THB',
                        0.00, 'THB', 'card', N'insurance'),
                       (@legacyOrder, @merchantA, NULL, 1, @legacyUser, @legacyOrderNo, 7, 1,
                        SYSUTCDATETIME(), SYSUTCDATETIME(), 1, 0, NULL, NULL, NULL, NULL,
                        N'Legacy own', N'0800000003', 100.00, 'THB', 100.00, 'THB', 0.00, 'THB',
                        0.00, 'THB', 'card', N'insurance'),
                       (@legacyOtherOrder, @merchantA, NULL, 1, @legacyOther, @legacyOtherOrderNo, 7, 1,
                        SYSUTCDATETIME(), SYSUTCDATETIME(), 1, 0, NULL, NULL, NULL, NULL,
                        N'Legacy other', N'0800000004', 100.00, 'THB', 100.00, 'THB', 0.00, 'THB',
                        0.00, 'THB', 'card', N'insurance');
                """,
                ("@accountA", accountA), ("@accountB", accountB), ("@accountC", accountC),
                ("@legacyUser", legacyUserId), ("@legacyOther", legacyOtherUserId),
                ("@legacySubject", $"legacy-{legacyUserId:N}"), ("@legacyOtherSubject", $"legacy-other-{legacyOtherUserId:N}"),
                ("@legacyTokenHash", legacyTokenHash),
                ("@accessA", accessA), ("@accessB", accessB),
                ("@merchantA", merchantA), ("@merchantB", merchantB), ("@roleA", roleA), ("@roleB", roleB),
                ("@codeA", $"filter-a-role-{runTag}"[..24]), ("@codeB", $"filter-b-role-{runTag}"[..24]),
                ("@orderA", orderA), ("@orderB", orderB), ("@orderC", orderC),
                ("@legacyOrder", legacyOrder), ("@legacyOtherOrder", legacyOtherOrder),
                ("@orderNoA", $"FA{orderA:N}"[..12]), ("@orderNoB", $"FB{orderB:N}"[..12]),
                ("@orderNoC", $"FC{orderC:N}"[..12]), ("@legacyOrderNo", $"FL{legacyOrder:N}"[..12]),
                ("@legacyOtherOrderNo", $"FO{legacyOtherOrder:N}"[..12]));
        }

        try
        {
            using var factory = new BffCanonicalFactory(database);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            async Task<ApiHost::Api.IdentityAccess.BffSessionIssue> IssueAsync(Guid accountId, Guid merchantId)
            {
                using var scope = factory.Services.CreateScope();
                var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                accessor.HttpContext = new DefaultHttpContext();
                var identities = scope.ServiceProvider.GetRequiredService<Accounts.Application.IIdentityAccessQuery>();
                var account = await identities.FindAccountAsync(accountId, default)
                    ?? throw new InvalidOperationException("The filter account was not found.");
                var manager = scope.ServiceProvider.GetRequiredService<ApiHost::Api.IdentityAccess.BffSessionManager>();
                var issue = await manager.CreateAsync(account, null, null, null, merchantId, "/api/v1/orders", default);
                accessor.HttpContext = null;
                return issue;
            }

            static IReadOnlySet<Guid> OrderIds(string body) =>
                JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("orderId").GetGuid()).ToHashSet();

            var sessionA = await IssueAsync(accountA, merchantA);
            using var identityA = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders?merchantId={merchantA:D}");
            identityA.Headers.Add("Cookie", Cookies(sessionA));
            using var identityAResponse = await client.SendAsync(identityA);
            Assert.Equal(HttpStatusCode.OK, identityAResponse.StatusCode);
            var identityAIds = OrderIds(await identityAResponse.Content.ReadAsStringAsync());
            Assert.Contains(orderA, identityAIds);
            Assert.Contains(orderC, identityAIds);
            Assert.DoesNotContain(orderB, identityAIds);

            using var crossCreatorDetail = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderC:D}?merchantId={merchantA:D}");
            crossCreatorDetail.Headers.Add("Cookie", Cookies(sessionA));
            using var crossCreatorDetailResponse = await client.SendAsync(crossCreatorDetail);
            Assert.Equal(HttpStatusCode.OK, crossCreatorDetailResponse.StatusCode);

            using var crossCreatorPatch = new HttpRequestMessage(
                HttpMethod.Patch, $"/api/v1/orders/{orderC:D}?merchantId={merchantA:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "insurance",
                    items = new[] { new { productReference = "cross-creator-patch", quantity = 1 } },
                }),
            };
            crossCreatorPatch.Headers.Add("Cookie", Cookies(sessionA));
            crossCreatorPatch.Headers.Add(ApiHost::Api.IdentityAccess.BffSessionManager.HeaderName, sessionA.CsrfToken);
            crossCreatorPatch.Headers.Add("If-Match", "\"v1\"");
            using var crossCreatorPatchResponse = await client.SendAsync(crossCreatorPatch);
            Assert.True(crossCreatorPatchResponse.StatusCode == HttpStatusCode.OK,
                await crossCreatorPatchResponse.Content.ReadAsStringAsync());
            await using (var creatorProbe = await Integration.Tests.IntegrationDb.OpenAsync(
                             Integration.Tests.IntegrationDb.SaConnFor(database)))
            {
                Assert.Equal(accountC.ToString("D"), (await Integration.Tests.IntegrationDb.ScalarAsync(
                    creatorProbe, "SELECT CreatedByAccountId FROM shop.Orders WHERE Id=@order;", ("@order", orderC)))?.ToString());
            }

            foreach (var childPath in new[]
            {
                $"/api/v1/orders/{orderC:D}/items?merchantId={merchantA:D}",
                $"/api/v1/orders/{orderC:D}/history?merchantId={merchantA:D}",
            })
            {
                using var identityChild = new HttpRequestMessage(HttpMethod.Get, childPath);
                identityChild.Headers.Add("Cookie", Cookies(sessionA));
                using var identityChildResponse = await client.SendAsync(identityChild);
                Assert.Equal(HttpStatusCode.OK, identityChildResponse.StatusCode);
            }

            using var crossTenantDetail = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/orders/{orderB:D}?merchantId={merchantA:D}");
            crossTenantDetail.Headers.Add("Cookie", Cookies(sessionA));
            using var crossTenantDetailResponse = await client.SendAsync(crossTenantDetail);
            Assert.Equal(HttpStatusCode.NotFound, crossTenantDetailResponse.StatusCode);

            using var legacyConsole = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders?merchantId={merchantA:D}");
            var legacySessionCookie = ApiHost::Api.Merchants.UserSessionCookies.SessionCookieNameDevHttp;
            legacyConsole.Headers.Add("Cookie", $"{legacySessionCookie}={legacyToken}");
            using var legacyResponse = await client.SendAsync(legacyConsole);
            Assert.Equal(HttpStatusCode.OK, legacyResponse.StatusCode);
            var legacyIds = OrderIds(await legacyResponse.Content.ReadAsStringAsync());
            Assert.Equal([legacyOrder], legacyIds);

            using var merchantUserPatch = new HttpRequestMessage(
                HttpMethod.Patch, $"/api/v1/orders/{orderC:D}?merchantId={merchantA:D}")
            {
                Content = JsonContent.Create(new
                {
                    businessType = "insurance",
                    items = new[] { new { productReference = "merchant-user-must-deny", quantity = 1 } },
                }),
            };
            merchantUserPatch.Headers.Add("Cookie", $"{legacySessionCookie}={legacyToken}");
            merchantUserPatch.Headers.Add("If-Match", "\"v1\"");
            using var merchantUserPatchResponse = await client.SendAsync(merchantUserPatch);
            Assert.True(merchantUserPatchResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                await merchantUserPatchResponse.Content.ReadAsStringAsync());

            foreach (var childPath in new[]
            {
                $"/api/v1/orders/{orderC:D}/items?merchantId={merchantA:D}",
                $"/api/v1/orders/{orderC:D}/history?merchantId={merchantA:D}",
            })
            {
                using var merchantUserChild = new HttpRequestMessage(HttpMethod.Get, childPath);
                merchantUserChild.Headers.Add("Cookie", $"{legacySessionCookie}={legacyToken}");
                using var merchantUserChildResponse = await client.SendAsync(merchantUserChild);
                Assert.True(merchantUserChildResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    await merchantUserChildResponse.Content.ReadAsStringAsync());
            }

            var sessionB = await IssueAsync(accountB, merchantB);
            using var identityB = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/orders?merchantId={merchantB:D}");
            identityB.Headers.Add("Cookie", Cookies(sessionB));
            using var identityBResponse = await client.SendAsync(identityB);
            Assert.Equal(HttpStatusCode.OK, identityBResponse.StatusCode);
            Assert.Equal([orderB], OrderIds(await identityBResponse.Content.ReadAsStringAsync()));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-4.1")]
    public async Task Marked_order_route_requires_the_console_permission_before_handler_execution()
    {
        var database = $"PolPr253ConsoleSweep{Guid.NewGuid():N}";
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.CreateScratchDatabaseAsync(database);
        await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.MigrateScratchDatabaseAsync(database);
        var orderId = Guid.CreateVersion7();
        var linkId = Guid.CreateVersion7();
        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/v1/orders", null),
            (HttpMethod.Get, $"/api/v1/orders/{orderId:D}", null),
            (HttpMethod.Patch, $"/api/v1/orders/{orderId:D}", new
            {
                businessType = "insurance",
                items = new[] { new { productReference = "permission-test", quantity = 1 } },
            }),
            (HttpMethod.Get, $"/api/v1/orders/{orderId:D}/items", null),
            (HttpMethod.Get, $"/api/v1/orders/{orderId:D}/history", null),
            (HttpMethod.Get, $"/api/v1/orders/{orderId:D}/payment-links", null),
            (HttpMethod.Post, $"/api/v1/orders/{orderId:D}/issue", null),
            (HttpMethod.Post, $"/api/v1/orders/{orderId:D}/payment-links", new { sendNotification = false }),
            (HttpMethod.Post, $"/api/v1/payment-links/{linkId:D}/revoke", new { reason = "permission-test" }),
            (HttpMethod.Post, $"/api/v1/orders/{orderId:D}/cancel", new { reason = "permission-test" }),
        };

        try
        {
            using var factory = new DenyConsolePermissionFactory(database);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            foreach (var (method, path, body) in routes)
            {
                using var request = new HttpRequestMessage(method, path)
                {
                    Content = body is null ? null : JsonContent.Create(body),
                };
                AddAdminHeaders(request, $"console-sweep-{Guid.CreateVersion7():N}",
                    method == HttpMethod.Patch || method == HttpMethod.Post ? "\"v1\"" : null);
                using var response = await client.SendAsync(request);
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                    $"{method} {path} returned {(int)response.StatusCode}: {responseBody}");
            }

            await using var verify = await Integration.Tests.IntegrationDb.OpenAsync(
                Integration.Tests.IntegrationDb.SaConnFor(database));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM shop.Orders;")));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM checkout.PaymentLinks;")));
            Assert.Equal(0, Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(
                verify, "SELECT COUNT(*) FROM txn.OutboxMessages;")));
        }
        finally
        {
            await Integration.Tests.PaymentCapabilitySchemaIntegrationTests.DropScratchDatabaseAsync(database);
        }
    }

    private static string Cookies(ApiHost::Api.IdentityAccess.BffSessionIssue issue)
    {
        var sessionCookie = ApiHost::Api.IdentityAccess.BffSessionManager.SessionCookieNameDevHttp;
        var csrfCookie = ApiHost::Api.IdentityAccess.BffSessionManager.CsrfCookieName;
        return $"{sessionCookie}={issue.SessionToken}; {csrfCookie}={issue.CsrfToken}";
    }

    private static void AddAdminHeaders(HttpRequestMessage request, string key, string? etag = null)
    {
        const string csrf = "pr253-role-admin-csrf";
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        var csrfHeader = ApiHost::Api.Admins.CsrfFilter.HeaderName;
        var csrfCookie = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add(csrfHeader, csrf);
        request.Headers.Add("Cookie", $"{csrfCookie}={csrf}");
        request.Headers.Add("Idempotency-Key", key);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }

    private static HttpRequestMessage Request(Guid merchantId, string session, string csrfCookie, string? csrfHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders?merchantId={merchantId:D}")
        {
            Content = JsonContent.Create(new
            {
                businessType = "insurance",
                currency = "THB",
                issueNow = false,
                items = new[]
                {
                    new
                    {
                        productReference = "SKU-BFF",
                        productCode = "SKU-BFF",
                        productName = "Bff price",
                        quantity = 1,
                        unitPrice = "100.0000",
                        discountAmount = "0.0000",
                        taxAmount = "0.0000",
                        lineAmount = "100.0000",
                    },
                },
            }),
        };
        request.Headers.Add("Cookie", $"pol_session={session}; pol_csrf={csrfCookie}");
        request.Headers.Add("Idempotency-Key", $"bff-order-{Guid.NewGuid():N}");
        if (csrfHeader is not null)
            request.Headers.Add("X-CSRF-Token", csrfHeader);
        return request;
    }
}

file sealed class BffCanonicalFactory(string database) : Task8A1SqlFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var connection = Integration.Tests.IntegrationDb.AppConnFor(database);
        builder.UseSetting("ConnectionStrings:App", connection);
        builder.UseSetting("ConnectionStrings:Admin", connection);
        builder.UseSetting("ConnectionStrings:Platform", connection);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ITrustedOrderPricingSource>();
            services.AddScoped<ITrustedOrderPricingSource, BffTrustedPricing>();
            services.PostConfigure<PolicySchemeOptions>(
                ApiHost::Api.Iam.ConsoleSessionAuthentication.SchemeName,
                options => options.ForwardDefaultSelector = context =>
                {
                    if (context.Request.Headers.ContainsKey(Task8A1AdminAuthHandler.Header))
                    {
                        context.Features.Set(new ApiHost::Api.Iam.SelectedConsoleAudience(
                            ApiHost::Api.Iam.ConsoleAudience.Admin));
                        return Task8A1AdminAuthHandler.SchemeName;
                    }
                    return ApiHost::Api.Iam.ConsoleSessionAuthentication.SelectScheme(context);
                });
        });
    }
}

file sealed class DenyConsolePermissionFactory(string database) : Task8A1SqlFactory
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        var connection = Integration.Tests.IntegrationDb.AppConnFor(database);
        builder.UseSetting("ConnectionStrings:App", connection);
        builder.UseSetting("ConnectionStrings:Admin", connection);
        builder.UseSetting("ConnectionStrings:Platform", connection);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAdminScope>();
            services.AddScoped<IAdminScope, DeniedAdminScope>();
            services.PostConfigure<Microsoft.AspNetCore.Authentication.PolicySchemeOptions>(
                ApiHost::Api.Iam.ConsoleSessionAuthentication.SchemeName,
                options => options.ForwardDefaultSelector = context =>
                {
                    context.Features.Set(new ApiHost::Api.Iam.SelectedConsoleAudience(
                        ApiHost::Api.Iam.ConsoleAudience.Admin));
                    return Task8A1AdminAuthHandler.SchemeName;
                });
        });
    }
}

file sealed class DeniedAdminScope : IAdminScope
{
    public bool IsBound => true;
    public Resolution Current { get; } = new(
        Task8A1SqlFactory.AdminId,
        "denied@example.test",
        Tier.Super,
        AccessibleMerchants.All)
    {
        Permissions = new HashSet<string>(StringComparer.Ordinal),
        AuthorizationVersion = 0,
    };
    public AccessibleMerchants Accessible => Current.Accessible;
}

file sealed class BffTrustedPricing : ITrustedOrderPricingSource
{
    public Task<TrustedOrderPricing> PriceAsync(
        Guid merchantId, string businessType, IReadOnlyList<OrderItemRequest> requestedItems,
        CancellationToken cancellationToken) => Task.FromResult(new TrustedOrderPricing(
            "THB",
            requestedItems.Select(x => new TrustedOrderLineInput(
                x.ProductReference, "card", "Bff price", x.Quantity,
                Money.Of(100m, "THB"), Money.Zero("THB"), Money.Zero("THB"),
                Money.Of(100m * x.Quantity, "THB"), "bff-sql-test")).ToArray(),
            Money.Zero("THB"), Money.Zero("THB")));
}
