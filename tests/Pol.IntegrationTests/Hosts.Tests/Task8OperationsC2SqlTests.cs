extern alias ApiHost;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using Admins.Application;
using Admins.Application.Users;
using BuildingBlocks.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Notifications.Application;
using Persistence.MerchantRuntime;
using Persistence.MerchantRuntime.Notifications;
using SharedKernel;

namespace Hosts.Tests;

[Trait("Capability", "ApiOperations")]
[Collection("ApiOperationsSql")]
[Trait("Category", "Integration")]
public sealed class Task8OperationsC2SqlTests
{
    private static readonly DateTime Now = new(2026, 9, 10, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Requirement", "REQ-9.2")]
    [Trait("Requirement", "REQ-9.3")]
    [Trait("Requirement", "REQ-9.4")]
    [Trait("Requirement", "REQ-9.5")]
    [Trait("Requirement", "REQ-9.6")]
    [Trait("Requirement", "REQ-9.7")]
    [Trait("Requirement", "REQ-9.8")]
    [Trait("Requirement", "REQ-9.9")]
    [Trait("Requirement", "REQ-9.10")]
    [Trait("Requirement", "REQ-10.2")]
    [Trait("Requirement", "REQ-10.3")]
    [Trait("Requirement", "REQ-10.6")]
    [Trait("Requirement", "REQ-10.7")]
    public async Task Notification_reads_retry_receipt_and_event_endpoint_use_real_scope_and_replay()
    {
        var merchantA = Guid.CreateVersion7();
        var merchantB = Guid.CreateVersion7();
        C2TestState.MerchantA = merchantA;
        C2TestState.MerchantB = merchantB;
        using var factory = new C2SqlFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var runTag = Guid.NewGuid().ToString("N");
        var sourceA = Guid.CreateVersion7();
        var sourceB = Guid.CreateVersion7();

        await using (var seed = await Integration.Tests.IntegrationDb.OpenAsync(
                         Integration.Tests.IntegrationDb.SaConn))
        {
            await EnsureMerchantAsync(seed, merchantA, $"c2a-{runTag}"[..20]);
            await EnsureMerchantAsync(seed, merchantB, $"c2b-{runTag}"[..20]);
        }

        var deliveryA = await MaterializeAsync(merchantA, sourceA,
            $"notify-a-{runTag}@example.test");
        var deliveryB = await MaterializeAsync(merchantB, sourceB,
            $"notify-b-{runTag}@example.test");

        try
        {
            await SetDeliveryStateAsync(deliveryA, status: 8, attemptCount: 1);
            await SetDeliveryStateAsync(deliveryB, status: 5, attemptCount: 1);

            using var list = await SendAdminAsync(client, HttpMethod.Get,
                $"/api/v1/notifications?merchantId={merchantA:D}&page=1&limit=10",
                runTag);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var listBody = await list.Content.ReadAsStringAsync();
            Assert.Contains(sourceA.ToString("D"), listBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain($"notify-a-{runTag}@example.test", listBody, StringComparison.Ordinal);

            using var detail = await SendAdminAsync(client, HttpMethod.Get,
                $"/api/v1/notifications/{sourceA:D}", runTag);
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

            var notificationId = await ReadNotificationIdAsync(sourceA);
            using var notification = await SendAdminAsync(client, HttpMethod.Get,
                $"/api/v1/notifications/{notificationId:D}", runTag);
            Assert.Equal(HttpStatusCode.OK, notification.StatusCode);
            Assert.DoesNotContain($"notify-a-{runTag}@example.test",
                await notification.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var delivery = await SendAdminAsync(client, HttpMethod.Get,
                $"/api/v1/notification-deliveries/{deliveryA:D}", runTag);
            Assert.Equal(HttpStatusCode.OK, delivery.StatusCode);
            using var attempts = await SendAdminAsync(client, HttpMethod.Get,
                $"/api/v1/notification-deliveries/{deliveryA:D}/attempts", runTag);
            Assert.Equal(HttpStatusCode.OK, attempts.StatusCode);

            using var crossMerchant = await SendAdminAsync(client, HttpMethod.Get,
                $"/api/v1/notification-deliveries/{deliveryB:D}", runTag);
            Assert.Equal(HttpStatusCode.NotFound, crossMerchant.StatusCode);

            var retry = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/notification-deliveries/{deliveryA:D}/retries")
            {
                Content = JsonContent.Create(new { reason = "operator retry" }),
            };
            AddAdminHeaders(retry, $"retry-{runTag}");
            using var retried = await client.SendAsync(retry);
            var retriedBody = await retried.Content.ReadAsStringAsync();
            Assert.True(retried.StatusCode == HttpStatusCode.Accepted, retriedBody);
            Assert.Contains("PENDING", retriedBody, StringComparison.Ordinal);

            var retryReplay = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/notification-deliveries/{deliveryA:D}/retries")
            {
                Content = JsonContent.Create(new { reason = "operator retry" }),
            };
            AddAdminHeaders(retryReplay, $"retry-{runTag}");
            using var replayed = await client.SendAsync(retryReplay);
            Assert.Equal(HttpStatusCode.Accepted, replayed.StatusCode);

            var retryChanged = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v1/notification-deliveries/{deliveryA:D}/retries")
            {
                Content = JsonContent.Create(new { reason = "changed intent" }),
            };
            AddAdminHeaders(retryChanged, $"retry-{runTag}");
            using var changed = await client.SendAsync(retryChanged);
            Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
            Assert.Contains("idempotency_key_reused", await changed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var receiptInvalid = new HttpRequestMessage(HttpMethod.Post,
                "/api/v1/webhooks/notifications/capture")
            {
                Content = JsonContent.Create(new
                {
                    deliveryId = deliveryB,
                    outcome = "unknown",
                    providerMessageId = $"receipt-{runTag}",
                }),
            };
            receiptInvalid.Headers.Add("X-Signature", "bad");
            using var invalid = await client.SendAsync(receiptInvalid);
            Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);

            var receipt = new HttpRequestMessage(HttpMethod.Post,
                "/api/v1/webhooks/notifications/capture")
            {
                Content = JsonContent.Create(new
                {
                    deliveryId = deliveryB,
                    outcome = "unknown",
                    providerMessageId = $"receipt-{runTag}",
                    failureCode = "provider_timeout",
                }),
            };
            receipt.Headers.Add("X-Signature", "capture-signature");
            using var received = await client.SendAsync(receipt);
            var receivedBody = await received.Content.ReadAsStringAsync();
            Assert.True(received.StatusCode == HttpStatusCode.OK,
                receivedBody + "\n" + string.Join("\n", C2Diagnostics.Messages));
            Assert.Contains("UNKNOWN", receivedBody, StringComparison.Ordinal);

            var receiptReplay = new HttpRequestMessage(HttpMethod.Post,
                "/api/v1/webhooks/notifications/capture")
            {
                Content = JsonContent.Create(new
                {
                    deliveryId = deliveryB,
                    outcome = "unknown",
                    providerMessageId = $"receipt-{runTag}",
                    failureCode = "provider_timeout",
                }),
            };
            receiptReplay.Headers.Add("X-Signature", "capture-signature");
            using var receivedReplay = await client.SendAsync(receiptReplay);
            Assert.Equal(HttpStatusCode.OK, receivedReplay.StatusCode);
            Assert.Contains("true", await receivedReplay.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            var unsafeEndpoint = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/merchants/{merchantA:D}/event-endpoint")
            {
                Content = JsonContent.Create(new
                {
                    url = "https://localhost/hook",
                    enabled = true,
                    signingKeyReference = "vault://business-events/key",
                }),
            };
            AddAdminHeaders(unsafeEndpoint, $"endpoint-unsafe-{runTag}");
            using var unsafeResponse = await client.SendAsync(unsafeEndpoint);
            Assert.Equal(HttpStatusCode.BadRequest, unsafeResponse.StatusCode);
            Assert.Contains("unsafe_destination", await unsafeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            var endpoint = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/merchants/{merchantA:D}/event-endpoint")
            {
                Content = JsonContent.Create(new
                {
                    url = "https://example.com/hook",
                    enabled = true,
                    signingKeyReference = "vault://business-events/key",
                }),
            };
            AddAdminHeaders(endpoint, $"endpoint-create-{runTag}");
            using var endpointCreated = await client.SendAsync(endpoint);
            var endpointBody = await endpointCreated.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, endpointCreated.StatusCode);
            Assert.DoesNotContain("vault://business-events/key", endpointBody, StringComparison.Ordinal);
            var endpointJson = JsonNode.Parse(endpointBody)!.AsObject();
            var endpointEtag = endpointCreated.Headers.ETag?.Tag
                ?? $"\"v{endpointJson["version"]!.GetValue<long>()}\"";

            var endpointDisable = new HttpRequestMessage(HttpMethod.Put,
                $"/api/v1/merchants/{merchantA:D}/event-endpoint")
            {
                Content = JsonContent.Create(new
                {
                    url = (string?)null,
                    enabled = false,
                    signingKeyReference = (string?)null,
                }),
            };
            AddAdminHeaders(endpointDisable, $"endpoint-disable-{runTag}", endpointEtag);
            using var disabled = await client.SendAsync(endpointDisable);
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            Assert.Contains("false", await disabled.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

            using var audit = await SendAdminAsync(client, HttpMethod.Get,
                $"/api/v1/audit-logs?merchantId={merchantA:D}&page=1&limit=100",
                runTag);
            Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
            var auditBody = await audit.Content.ReadAsStringAsync();
            Assert.Contains("webhook-endpoint", auditBody, StringComparison.Ordinal);
            Assert.DoesNotContain("vault://business-events/key", auditBody, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(sourceA, sourceB, merchantA, merchantB);
        }
    }

    [Fact]
    [Trait("Requirement", "REQ-10.1")]
    [Trait("Requirement", "REQ-10.4")]
    [Trait("Requirement", "REQ-10.8")]
    public async Task Canonical_openapi_auth_schema_and_health_split_are_stable()
    {
        using var factory = new C2OpenApiFactory();
        using var client = factory.CreateClient();
        using var openapiResponse = await client.GetAsync("/openapi/v1.json");
        var openapiBody = await openapiResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, openapiResponse.StatusCode);
        using var document = JsonDocument.Parse(openapiBody);
        var paths = document.RootElement.GetProperty("paths");

        var retry = paths.GetProperty("/api/v1/notification-deliveries/{deliveryId}/retries")
            .GetProperty("post");
        Assert.Contains("security", retry.EnumerateObject().Select(x => x.Name));
        var retrySchemaRef = retry.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()!;
        var retrySchemaName = retrySchemaRef[(retrySchemaRef.LastIndexOf('/') + 1)..];
        var retrySchema = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(retrySchemaName);
        Assert.True(retrySchema.GetProperty("properties").TryGetProperty("reason", out _));
        Assert.Contains("reason", retrySchema.GetProperty("required").EnumerateArray()
            .Select(x => x.GetString()), StringComparer.Ordinal);

        var endpoint = paths.GetProperty("/api/v1/merchants/{merchantId}/event-endpoint")
            .GetProperty("put");
        var parameterNames = endpoint.GetProperty("parameters").EnumerateArray()
            .Select(x => x.GetProperty("name").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("If-Match", parameterNames);
        Assert.Contains("Idempotency-Key", parameterNames);

        var receipt = paths.GetProperty("/api/v1/webhooks/notifications/{providerCode}")
            .GetProperty("post");
        Assert.False(receipt.TryGetProperty("security", out _));

        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("healthy", JsonNode.Parse(await live.Content.ReadAsStringAsync())!["status"]!.ToString());
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        var readyBody = await ready.Content.ReadAsStringAsync();
        Assert.Contains("not_ready", readyBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", readyBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection string", readyBody, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<HttpResponseMessage> SendAdminAsync(
        HttpClient client, HttpMethod method, string path, string key, string? etag = null)
    {
        var request = new HttpRequestMessage(method, path);
        AddAdminHeaders(request, key, etag);
        return await client.SendAsync(request);
    }

    private static void AddAdminHeaders(HttpRequestMessage request, string key, string? etag = null)
    {
        request.Headers.Add(Task8A1AdminAuthHandler.Header, "yes");
        request.Headers.Add(ApiHost::Api.Admins.CsrfFilter.HeaderName, "csrf-c2");
        var csrfCookieName = ApiHost::Api.Admins.SessionCookies.CsrfCookieName;
        request.Headers.Add("Cookie", $"{csrfCookieName}=csrf-c2");
        request.Headers.Add("Idempotency-Key", key);
        if (etag is not null)
            request.Headers.Add("If-Match", etag);
    }

    private static async Task EnsureMerchantAsync(Microsoft.Data.SqlClient.SqlConnection connection, Guid id, string code)
    {
        if (Convert.ToInt32(await Integration.Tests.IntegrationDb.ScalarAsync(connection,
                "SELECT COUNT(*) FROM merch.Merchants WHERE Id=@id;", ("@id", id))) == 0)
            await Integration.Tests.IntegrationDb.InsertMerchantAsync(connection, id, code);
    }

    private static async Task<Guid> MaterializeAsync(Guid merchantId, Guid sourceEventId, string email)
    {
        await using var db = CreateRuntimeDb(merchantId);
        var materializer = new NotificationMaterializer(
            db,
            new MerchantRuntimeUnitOfWork(db, NoOpSecurityTelemetry.Instance),
            new FixedClock(Now));
        await materializer.MaterializeAsync(new NotificationEvent(
            sourceEventId,
            merchantId,
            "AgentRegistrationDecidedV1",
            "{\"c2\":true}",
            Now,
            email), default);
        await using var lookup = await Integration.Tests.IntegrationDb.OpenAsync(
            Integration.Tests.IntegrationDb.SaConn);
        return Guid.Parse(Convert.ToString(await Integration.Tests.IntegrationDb.ScalarAsync(lookup, """
            SELECT Id FROM txn.Deliveries
            WHERE SourceEventId=@event AND Channel=N'email';
            """, ("@event", sourceEventId)))!);
    }

    private static async Task<Guid> ReadNotificationIdAsync(Guid sourceEventId)
    {
        await using var lookup = await Integration.Tests.IntegrationDb.OpenAsync(
            Integration.Tests.IntegrationDb.SaConn);
        return Guid.Parse(Convert.ToString(await Integration.Tests.IntegrationDb.ScalarAsync(lookup, """
            SELECT Id FROM txn.Notifications WHERE SourceEventId=@event;
            """, ("@event", sourceEventId)))!);
    }

    private static async Task SetDeliveryStateAsync(Guid deliveryId, int status, int attemptCount)
    {
        await using var connection = await Integration.Tests.IntegrationDb.OpenAsync(
            Integration.Tests.IntegrationDb.SaConn);
        await Integration.Tests.IntegrationDb.ExecAsync(connection, """
            UPDATE txn.Deliveries
            SET Status=@status, AttemptCount=@attempts, NextAttemptAt=@at, CompletedAt=CASE WHEN @status=8 THEN @at ELSE NULL END
            WHERE Id=@id;
            """, ("@status", status), ("@attempts", attemptCount), ("@at", Now), ("@id", deliveryId));
    }

    private static async Task CleanupAsync(Guid sourceA, Guid sourceB, Guid merchantA, Guid merchantB)
    {
        await using var connection = await Integration.Tests.IntegrationDb.OpenAsync(
            Integration.Tests.IntegrationDb.SaConn);
        await Integration.Tests.IntegrationDb.ExecAsync(connection, """
            DELETE FROM admin.OperationRecords WHERE MerchantId IN (@a,@b);
            DELETE FROM admin.AuditRecords WHERE MerchantId IN (@a,@b);
            DELETE FROM admin.AuditHeads WHERE MerchantId IN (@a,@b);
            DELETE FROM admin.DeliverySecretVersions WHERE MerchantId IN (@a,@b);
            DELETE FROM admin.WebhookEndpoints WHERE MerchantId IN (@a,@b);
            DELETE FROM txn.DeliveryAttempts WHERE MerchantId IN (@a,@b);
            DELETE FROM txn.Deliveries WHERE MerchantId IN (@a,@b);
            DELETE FROM txn.Notifications WHERE MerchantId IN (@a,@b);
            DELETE FROM txn.NotificationInboxMessages WHERE MerchantId IN (@a,@b);
            DELETE attempts
            FROM txn.DeliveryAttempts attempts
            INNER JOIN txn.Deliveries deliveries ON deliveries.Id = attempts.DeliveryId
            WHERE deliveries.TemplateVersionId IN (
                SELECT Id FROM txn.TemplateVersions WHERE EventType=N'AgentRegistrationDecidedV1');
            DELETE FROM txn.Deliveries
            WHERE TemplateVersionId IN (
                SELECT Id FROM txn.TemplateVersions WHERE EventType=N'AgentRegistrationDecidedV1');
            DELETE FROM txn.TemplateVersions WHERE EventType=N'AgentRegistrationDecidedV1';
            DELETE FROM merch.Merchants WHERE Id IN (@a,@b);
            """, ("@a", merchantA), ("@b", merchantB));
    }

    private static MerchantRuntimeDbContext CreateRuntimeDb(Guid merchantId)
    {
        var options = new DbContextOptionsBuilder<MerchantRuntimeDbContext>()
            .UseSqlServer(Integration.Tests.IntegrationDb.AppConn, sql => sql.UseCompatibilityLevel(170))
            .Options;
        return new MerchantRuntimeDbContext(
            options,
            new C2Actor(merchantId),
            C2AllowAllWriteAuthorizer.Instance,
            NoOpSecurityTelemetry.Instance);
    }

    private sealed class FixedClock(DateTime now) : IClock
    {
        public DateTime UtcNow => now;
    }

    private sealed class C2Actor(Guid merchantId) : IActorContext
    {
        public Guid MerchantId => merchantId;
        public Guid? UserId => Task8A1SqlFactory.AdminId;
        public bool HasActor => true;
        public string? SaleCode => null;
    }

    private sealed class C2AllowAllWriteAuthorizer : IWriteAuthorizer
    {
        public static readonly C2AllowAllWriteAuthorizer Instance = new();
        public bool CanWrite(Type entityType, WriteOperation operation, Guid targetMerchant) => true;
    }
}

file sealed class C2SqlFactory : Task8A1SqlFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAdminScope>();
            services.AddScoped<IAdminScope, C2RestrictedAdminScope>();
            services.RemoveAll<INotificationReceiptVerifier>();
            services.AddSingleton<INotificationReceiptVerifier, C2CaptureReceiptVerifier>();
        });
        builder.ConfigureLogging(logging => logging.AddProvider(new C2LoggerProvider()));
    }
}

file static class C2Diagnostics
{
    public static readonly ConcurrentQueue<string> Messages = new();
}

file sealed class C2LoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new C2Logger(categoryName);
    public void Dispose() { }

    private sealed class C2Logger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                C2Diagnostics.Messages.Enqueue($"{category}: {formatter(state, exception)}\n{exception}");
        }
    }
}

file sealed class C2RestrictedAdminScope(IHttpContextAccessor accessor) : IAdminScope
{
    public bool IsBound => accessor.HttpContext?.Request.Headers.ContainsKey(Task8A1AdminAuthHandler.Header) == true;
    public Resolution Current { get; } = new(
        Task8A1SqlFactory.AdminId,
        "task8-c2@example.test",
        Admins.Domain.Users.Tier.Scoped,
        AccessibleMerchants.Of(new HashSet<Guid> { C2TestState.MerchantA }))
    {
        Permissions = Iam.Domain.Permissions.Keys.AllKeys,
        AuthorizationVersion = 0,
    };
    public AccessibleMerchants Accessible => Current.Accessible;
}

file static class C2TestState
{
    public static Guid MerchantA { get; set; }
    public static Guid MerchantB { get; set; }
}

file sealed class C2CaptureReceiptVerifier : INotificationReceiptVerifier
{
    public async Task<NotificationReceiptVerification> VerifyAsync(
        string providerCode, string rawPayload, string signature, CancellationToken cancellationToken)
    {
        if (!string.Equals(providerCode, "capture", StringComparison.Ordinal)
            || !string.Equals(signature, "capture-signature", StringComparison.Ordinal))
            return NotificationReceiptVerification.Invalid("webhook_signature_invalid");
        var body = await JsonSerializer.DeserializeAsync<C2ReceiptBody>(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rawPayload)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken);
        if (body is null || body.DeliveryId == Guid.Empty || string.IsNullOrWhiteSpace(body.ProviderMessageId)
            || !Enum.TryParse<NotificationReceiptOutcome>(body.Outcome, true, out var outcome))
            return NotificationReceiptVerification.Invalid("validation_failed");
        return NotificationReceiptVerification.Valid(new NotificationReceipt(
            body.DeliveryId, outcome, body.ProviderMessageId, body.FailureCode));
    }

    private sealed record C2ReceiptBody(
        Guid DeliveryId, string Outcome, string ProviderMessageId, string? FailureCode);
}

file sealed class C2OpenApiFactory : WebApplicationFactory<ApiHost::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Development);
        builder.UseSetting("ConnectionStrings:Migrator", "");
        builder.UseSetting("ConnectionStrings:App", "Server=(local);Database=pol_test;Trusted_Connection=True;");
            builder.ConfigureServices(services => services.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Vault:MasterKeyBase64"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
        }));
    }
}
