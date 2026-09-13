extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BuildingBlocks.Application;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Notifications.Application;
using Notifications.Domain;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Notifications;

namespace Hosts.Tests;

[Collection("ApiOperationsSql")]
[Trait("Category", "Integration")]
public sealed class Task8IdentityAccessA2SqlTests
{
    [Fact]
    [Trait("Capability", "ApiOperations")]
    [Trait("Requirement", "REQ-2")]
    [Trait("Requirement", "REQ-3")]
    [Trait("Requirement", "REQ-10")]
    public async Task Account_role_access_and_platform_lifecycle_uses_real_sql_and_guards()
    {
        using var factory = new Task8A1SqlFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        var branchA = Guid.CreateVersion7();
        var branchB = Guid.CreateVersion7();
        var agentAccount = Guid.CreateVersion7();
        var employeeAccount = Guid.CreateVersion7();
        var merchantRole = Guid.CreateVersion7();
        var runTag = Guid.NewGuid().ToString("N");
        var roleId = Guid.Empty;

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConn))
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantA, $"a2a-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(seed, merchantB, $"a2b-{runTag}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(seed, """
                INSERT merch.Branches (Id, MerchantId, Code, Name, Status, CreatedAt, UpdatedAt, Version)
                VALUES (@branchA, @merchantA, @codeA, N'A2 branch A', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1),
                       (@branchB, @merchantB, @codeB, N'A2 branch B', 1, SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                INSERT acct.Accounts (Id, AccountType, DisplayName, Status, AuthorizationVersion, CreatedAt, UpdatedAt)
                VALUES (@agent, 2, N'A2 Agent', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@employee, 1, N'A2 Employee', 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT acct.Employees (AccountId, EmployeeCode, DepartmentCode, Metadata, Id)
                VALUES (@employee, N'A2-EMP', N'OPS', N'{}', NEWID());
                INSERT iam.Roles (Id, Code, Name, Description, Color, Status, Scope, MerchantId)
                VALUES (@merchantRole, @roleCode, N'A2 Merchant Role', NULL, N'blue', 1, 2, @merchantA);
                INSERT iam.RolePermissions (Id, RoleId, PermissionKey)
                VALUES (NEWID(), @merchantRole, N'payment.view');
                """,
                ("@branchA", branchA), ("@branchB", branchB), ("@merchantA", merchantA), ("@merchantB", merchantB),
                ("@codeA", $"a-{runTag}"), ("@codeB", $"b-{runTag}"), ("@agent", agentAccount),
                ("@employee", employeeAccount), ("@merchantRole", merchantRole), ("@roleCode", $"a2_role_{runTag}"));
        }

        try
        {
            using var accountList = await SendAsync(client, HttpMethod.Get, "/api/v1/accounts?limit=100");
            Assert.Equal(HttpStatusCode.OK, accountList.StatusCode);
            Assert.Contains(agentAccount.ToString("D"), await accountList.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            using var accountRead = await SendAsync(client, HttpMethod.Get, $"/api/v1/accounts/{agentAccount:D}");
            Assert.Equal(HttpStatusCode.OK, accountRead.StatusCode);
            var accountEtag = accountRead.Headers.ETag!.Tag;

            var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/accounts/{agentAccount:D}")
            {
                Content = JsonContent.Create(new { displayName = "A2 Agent Suspended", status = "Suspended" }),
            };
            AddAdminHeaders(patch, $"account-patch-{runTag}", accountEtag);
            using var patched = await client.SendAsync(patch);
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
            var activeEtag = patched.Headers.ETag!.Tag;

            var patchReplay = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/accounts/{agentAccount:D}")
            {
                Content = JsonContent.Create(new { displayName = "A2 Agent Suspended", status = "Suspended" }),
            };
            AddAdminHeaders(patchReplay, $"account-patch-{runTag}", accountEtag);
            using var replayed = await client.SendAsync(patchReplay);
            Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);

            var patchChanged = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/accounts/{agentAccount:D}")
            {
                Content = JsonContent.Create(new { displayName = "A2 Agent Changed", status = "Suspended" }),
            };
            AddAdminHeaders(patchChanged, $"account-patch-{runTag}", accountEtag);
            using var changed = await client.SendAsync(patchChanged);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

            var reactivate = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/accounts/{agentAccount:D}")
            {
                Content = JsonContent.Create(new { displayName = "A2 Agent Active", status = "Active" }),
            };
            AddAdminHeaders(reactivate, $"account-reactivate-{runTag}", activeEtag);
            using var active = await client.SendAsync(reactivate);
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);

            var revokeSessions = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/accounts/{agentAccount:D}/session-revocations")
            {
                Content = JsonContent.Create(new { reason = "a2-test" }),
            };
            AddAdminHeaders(revokeSessions, $"session-revoke-{runTag}");
            using var revoked = await client.SendAsync(revokeSessions);
            Assert.Equal(HttpStatusCode.Accepted, revoked.StatusCode);

            var roleCreate = new HttpRequestMessage(HttpMethod.Post, "/api/v1/roles")
            {
                Content = JsonContent.Create(new
                {
                    code = $"a2_platform_{runTag}", name = "A2 Platform Role", status = "Active",
                    permissions = new[] { "user.manage" },
                }),
            };
            AddAdminHeaders(roleCreate, $"role-create-{runTag}");
            using var roleCreated = await client.SendAsync(roleCreate);
            Assert.Equal(HttpStatusCode.Created, roleCreated.StatusCode);
            var roleJson = JsonNode.Parse(await roleCreated.Content.ReadAsStringAsync())!.AsObject();
            roleId = Guid.Parse(roleJson["id"]!.ToString());
            var roleEtag = roleCreated.Headers.ETag!.Tag;

            using var roleRead = await SendAsync(client, HttpMethod.Get, $"/api/v1/roles/{roleId:D}");
            Assert.Equal(HttpStatusCode.OK, roleRead.StatusCode);

            var roleUpdate = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/roles/{roleId:D}")
            {
                Content = JsonContent.Create(new
                {
                    name = "A2 Platform Role Updated", status = "Active", permissions = new[] { "user.manage" },
                }),
            };
            AddAdminHeaders(roleUpdate, $"role-update-{runTag}", roleEtag);
            using var roleUpdated = await client.SendAsync(roleUpdate);
            Assert.Equal(HttpStatusCode.OK, roleUpdated.StatusCode);

            var roleStale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/roles/{roleId:D}")
            {
                Content = JsonContent.Create(new
                {
                    name = "A2 stale", status = "Active", permissions = new[] { "user.manage" },
                }),
            };
            AddAdminHeaders(roleStale, $"role-stale-{runTag}", roleEtag);
            using var roleStaleResponse = await client.SendAsync(roleStale);
            Assert.Equal(HttpStatusCode.PreconditionFailed, roleStaleResponse.StatusCode);

            var access = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/accounts/{agentAccount:D}/merchant-access/{merchantA:D}")
            {
                Content = JsonContent.Create(new
                {
                    dataScope = "Merchant", roleIds = new[] { merchantRole },
                    branchIds = new[] { branchA }, paymentMethods = new[] { "card" },
                }),
            };
            AddAdminHeaders(access, $"access-{runTag}", "\"v0\"");
            using var accessCreated = await client.SendAsync(access);
            Assert.True(accessCreated.StatusCode == HttpStatusCode.OK,
                await accessCreated.Content.ReadAsStringAsync());
            var accessJson = JsonNode.Parse(await accessCreated.Content.ReadAsStringAsync())!.AsObject();
            var accessEtag = accessCreated.Headers.ETag!.Tag;

            using var accessList = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/accounts/{agentAccount:D}/merchant-access");
            Assert.Equal(HttpStatusCode.OK, accessList.StatusCode);
            Assert.Contains(merchantA.ToString("D"), await accessList.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            var accessReplay = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/accounts/{agentAccount:D}/merchant-access/{merchantA:D}")
            {
                Content = JsonContent.Create(new
                {
                    dataScope = "Merchant", roleIds = new[] { merchantRole },
                    branchIds = new[] { branchA }, paymentMethods = new[] { "card" },
                }),
            };
            AddAdminHeaders(accessReplay, $"access-{runTag}", "\"v0\"");
            using var accessReplayed = await client.SendAsync(accessReplay);
            Assert.Equal(HttpStatusCode.OK, accessReplayed.StatusCode);
            Assert.Equal(accessJson["accessId"]!.ToString(),
                JsonNode.Parse(await accessReplayed.Content.ReadAsStringAsync())!["accessId"]!.ToString());

            var crossBranch = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/accounts/{agentAccount:D}/merchant-access/{merchantA:D}")
            {
                Content = JsonContent.Create(new
                {
                    dataScope = "Merchant", roleIds = new[] { merchantRole },
                    branchIds = new[] { branchB }, paymentMethods = new[] { "card" },
                }),
            };
            AddAdminHeaders(crossBranch, $"cross-branch-{runTag}", accessEtag);
            using var crossBranchResponse = await client.SendAsync(crossBranch);
            Assert.Equal(HttpStatusCode.BadRequest, crossBranchResponse.StatusCode);

            var staleAccess = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/accounts/{agentAccount:D}/merchant-access/{merchantA:D}")
            {
                Content = JsonContent.Create(new
                {
                    dataScope = "Merchant", roleIds = new[] { merchantRole },
                    branchIds = new[] { branchA }, paymentMethods = new[] { "promptpay" },
                }),
            };
            AddAdminHeaders(staleAccess, $"stale-access-{runTag}", "\"v0\"");
            using var staleAccessResponse = await client.SendAsync(staleAccess);
            Assert.Equal(HttpStatusCode.PreconditionFailed, staleAccessResponse.StatusCode);

            var revokeAccess = new HttpRequestMessage(HttpMethod.Delete,
                $"/api/v1/accounts/{agentAccount:D}/merchant-access/{merchantA:D}");
            AddAdminHeaders(revokeAccess, $"revoke-access-{runTag}", accessEtag);
            using var revokedAccess = await client.SendAsync(revokeAccess);
            Assert.Equal(HttpStatusCode.NoContent, revokedAccess.StatusCode);

            var platformAccess = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/accounts/{employeeAccount:D}/platform-access")
            {
                Content = JsonContent.Create(new { status = "Active", roleIds = new[] { roleId } }),
            };
            AddAdminHeaders(platformAccess, $"platform-access-{runTag}", "\"v0\"");
            using var platform = await client.SendAsync(platformAccess);
            Assert.Equal(HttpStatusCode.OK, platform.StatusCode);

            using var platformRead = await SendAsync(client, HttpMethod.Get,
                $"/api/v1/accounts/{employeeAccount:D}/platform-access");
            Assert.Equal(HttpStatusCode.OK, platformRead.StatusCode);

            var agentPlatform = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/accounts/{agentAccount:D}/platform-access")
            {
                Content = JsonContent.Create(new { status = "Active", roleIds = new[] { roleId } }),
            };
            AddAdminHeaders(agentPlatform, $"agent-platform-{runTag}", "\"v0\"");
            using var agentPlatformResponse = await client.SendAsync(agentPlatform);
            Assert.Equal(HttpStatusCode.BadRequest, agentPlatformResponse.StatusCode);

            await using var audit = await Integration.Tests.IntegrationDb.OpenAsync(Integration.Tests.IntegrationDb.SaConn);
            var auditCount = await Integration.Tests.IntegrationDb.ScalarAsync(audit,
                "SELECT COUNT_BIG(*) FROM admin.UserAudits WHERE ActorId=@actor AND CorrelationId IS NOT NULL AND Action IN (N'account-updated', N'account-sessions-revoked', N'merchant-access-changed', N'platform-access-changed');",
                ("@actor", Task8A1SqlFactory.AdminId));
            Assert.True(Convert.ToInt64(auditCount) >= 4);
        }
        finally
        {
            await using var cleanup = await Integration.Tests.IntegrationDb.OpenAsync(Integration.Tests.IntegrationDb.SaConn);
            await Integration.Tests.IntegrationDb.ExecAsync(cleanup, """
                DELETE FROM access.AccessRoles WHERE MerchantAccessId IN (SELECT Id FROM access.MerchantAccess WHERE AccountId=@agent);
                DELETE FROM access.BranchAccess WHERE MerchantAccessId IN (SELECT Id FROM access.MerchantAccess WHERE AccountId=@agent);
                DELETE FROM access.MerchantAccessMethods WHERE MerchantAccessId IN (SELECT Id FROM access.MerchantAccess WHERE AccountId=@agent);
                DELETE FROM access.MerchantAccess WHERE AccountId=@agent;
                DELETE FROM access.PlatformAccessRoles WHERE PlatformAccessId IN (SELECT Id FROM access.PlatformAccess WHERE EmployeeAccountId=@employee);
                DELETE FROM access.PlatformAccess WHERE EmployeeAccountId=@employee;
                DELETE FROM acct.Employees WHERE AccountId=@employee;
                DELETE FROM acct.Accounts WHERE Id IN (@agent, @employee);
                DELETE FROM iam.RolePermissions WHERE RoleId IN (@merchantRole, @platformRole);
                DELETE FROM iam.Roles WHERE Id IN (@merchantRole, @platformRole);
                DELETE FROM admin.UserAudits WHERE ActorId=@actor AND Action IN (N'account-updated', N'account-sessions-revoked', N'merchant-access-changed', N'platform-access-changed');
                DELETE FROM merch.Sales WHERE MerchantId IN (@merchantA, @merchantB);
                DELETE FROM merch.Branches WHERE MerchantId IN (@merchantA, @merchantB);
                DELETE FROM merch.Merchants WHERE Id IN (@merchantA, @merchantB);
                """,
                ("@agent", agentAccount), ("@employee", employeeAccount), ("@merchantRole", merchantRole),
                ("@platformRole", roleId), ("@merchantA", merchantA), ("@merchantB", merchantB),
                ("@actor", Task8A1SqlFactory.AdminId));
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9")]
    [Trait("Capability", "ApiOperations")]
    public async Task Active_webhook_endpoint_index_is_filtered_unique_per_merchant()
    {
        var merchantId = Guid.CreateVersion7();
        var endpointA = Guid.CreateVersion7();
        var endpointB = Guid.CreateVersion7();
        var secretA = Guid.CreateVersion7();
        var secretB = Guid.CreateVersion7();
        await using var connection = await Integration.Tests.IntegrationDb.OpenAsync(Integration.Tests.IntegrationDb.SaConn);
        try
        {
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(connection, merchantId, $"wh-{Guid.NewGuid():N}"[..20]);
            await Integration.Tests.IntegrationDb.ExecAsync(connection, """
                INSERT admin.DeliverySecretVersions
                    (Id, OwnerId, MerchantId, OwnerType, ProtectedSecret, State, CreatedAt, ActivatedAt, RetiredAt)
                VALUES (@secretA, @endpointA, @merchant, N'webhook-endpoint', N'a', 2, SYSUTCDATETIME(), SYSUTCDATETIME(), NULL),
                       (@secretB, @endpointB, @merchant, N'webhook-endpoint', N'b', 2, SYSUTCDATETIME(), SYSUTCDATETIME(), NULL);
                INSERT admin.WebhookEndpoints
                    (Id, MerchantId, Name, Url, EventsCsv, Enabled, ActiveSecretVersionId, SecretHint, CreatedAt, UpdatedAt, Version)
                VALUES (@endpointA, @merchant, N'a', N'https://a.example/hook', N'payment.paid', 1, @secretA, N'••••a', SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                """,
                ("@secretA", secretA), ("@secretB", secretB), ("@endpointA", endpointA), ("@endpointB", endpointB),
                ("@merchant", merchantId));
            await Assert.ThrowsAsync<SqlException>(() => Integration.Tests.IntegrationDb.ExecAsync(connection, """
                INSERT admin.WebhookEndpoints
                    (Id, MerchantId, Name, Url, EventsCsv, Enabled, ActiveSecretVersionId, SecretHint, CreatedAt, UpdatedAt, Version)
                VALUES (@endpoint, @merchant, N'b', N'https://b.example/hook', N'payment.paid', 1, @secret, N'••••b', SYSUTCDATETIME(), SYSUTCDATETIME(), 1);
                """, ("@endpoint", endpointB), ("@merchant", merchantId), ("@secret", secretB)));
        }
        finally
        {
            await Integration.Tests.IntegrationDb.ExecAsync(connection,
                "DELETE FROM admin.WebhookEndpoints WHERE Id IN (@a,@b); DELETE FROM admin.DeliverySecretVersions WHERE Id IN (@sa,@sb); DELETE FROM merch.Merchants WHERE Id=@merchant;",
                ("@a", endpointA), ("@b", endpointB), ("@sa", secretA), ("@sb", secretB), ("@merchant", merchantId));
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-9")]
    [Trait("Capability", "ApiOperations")]
    public async Task Runtime_principal_materializes_claims_processes_delivery_and_writes_review_note()
    {
        var merchantId = Guid.CreateVersion7();
        var sourceEventId = Guid.CreateVersion7();
        var now = DateTime.UtcNow;
        await using var setup = await Integration.Tests.IntegrationDb.OpenAsync(Integration.Tests.IntegrationDb.SaConn);
        await Integration.Tests.IntegrationDb.InsertMerchantAsync(setup, merchantId, $"n-{Guid.NewGuid():N}"[..20]);

        try
        {
            await using var db = new MerchantRuntimeDbContext(
                new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
                    .UseSqlServer(Integration.Tests.IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
                    .Options,
                new RuntimeActor(merchantId), AllowAllWriter.Instance, NoOpSecurityTelemetry.Instance);
            var clock = new FixedClock(now);
            var uow = new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance);
            var materializer = new NotificationMaterializer(db, uow, clock);
            await materializer.MaterializeAsync(new NotificationEvent(
                sourceEventId, merchantId, "AgentRegistrationDecidedV1", "{\"ok\":true}", now,
                Email: "runtime@example.test", CorrelationId: "task8-runtime-correlation"), default);
            await materializer.MaterializeAsync(new NotificationEvent(
                sourceEventId, merchantId, "AgentRegistrationDecidedV1", "{\"ok\":true}", now,
                Email: "runtime@example.test", CorrelationId: "task8-runtime-correlation"), default);

            var notification = await db.Notifications.IgnoreQueryFilters()
                .SingleAsync(x => x.SourceEventId == sourceEventId);
            var delivery = await db.Deliveries.IgnoreQueryFilters()
                .SingleAsync(x => x.NotificationId == notification.Id);
            var operations = new NotificationOperations(db, uow, clock);
            var search = await operations.SearchAsync(
                new NotificationSearchQuery(1, 25, MerchantId: merchantId, CorrelationId: "task8-runtime-correlation"),
                new DeliveryAccess(false, new HashSet<Guid> { merchantId }), default);
            Assert.Single(search.Items);
            await operations.AddReviewNoteAsync(
                merchantId, notification.Id, delivery.Id, Task8A1SqlFactory.AdminId, "runtime review",
                new DeliveryAccess(false, new HashSet<Guid> { merchantId }), default);

            var processor = new NotificationDeliveryProcessor(
                db, uow, clock, new AcceptedEmailSender(), new NotConfiguredSmsSender());
            await processor.ProcessAsync(delivery.Id, default);
            Assert.Equal(DeliveryStatus.Accepted, (await db.Deliveries.IgnoreQueryFilters()
                .SingleAsync(x => x.Id == delivery.Id)).Status);
            Assert.Equal(1, await db.DeliveryAttempts.IgnoreQueryFilters()
                .CountAsync(x => x.DeliveryId == delivery.Id));
        }
        finally
        {
            await Integration.Tests.IntegrationDb.ExecAsync(setup,
                "DELETE FROM txn.NotificationReviewNotes WHERE MerchantId=@merchant; DELETE FROM txn.DeliveryAttempts WHERE MerchantId=@merchant; DELETE FROM txn.Deliveries WHERE MerchantId=@merchant; DELETE FROM txn.NotificationInboxMessages WHERE MerchantId=@merchant; DELETE FROM txn.Notifications WHERE MerchantId=@merchant; DELETE FROM txn.TemplateVersions WHERE EventType=N'AgentRegistrationDecidedV1'; DELETE FROM merch.Merchants WHERE Id=@merchant;",
                ("@merchant", merchantId));
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
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, "csrf-a2");
        var cookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{cookieName}=csrf-a2");
        request.Headers.Add("Idempotency-Key", key);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }

    private sealed class RuntimeActor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId { get; } = merchantId;
        public Guid? UserId => null;
        public bool HasActor => true;
        public string? SaleCode => null;
    }

    private sealed class AllowAllWriter : IWriteAuthorizer
    {
        public static readonly AllowAllWriter Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; } = now;
    }

    private sealed class AcceptedEmailSender : IEmailSenderPort
    {
        public Task<DeliveryProviderResult> SendAsync(
            EmailDeliveryRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DeliveryProviderResult(DeliveryProviderOutcome.Accepted, "task8-provider-message"));
    }
}
