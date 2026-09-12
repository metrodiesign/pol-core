using Admins.Application;
using Api.Iam;
using BuildingBlocks.Application;
using Governance.Application;
using Iam.Domain.Permissions;
using Notifications.Application;

namespace Api.Notifications;

internal static class CanonicalNotificationEndpoints
{
    private static readonly string[] BusinessEvents = ["payments.transaction-succeeded.v1"];

    public static void MapCanonicalNotificationEndpoints(this RouteGroupBuilder api)
    {
        MapNotificationReads(api);
        MapNotificationRetry(api);
        MapNotificationReceipt(api);
        MapBusinessEventEndpoint(api);
        MapAuditLogs(api);
    }

    private static void MapNotificationReads(RouteGroupBuilder api)
    {
        api.MapGet("/notifications", async (
            IAdminScope scope,
            INotificationOperations operations,
            int page = 1,
            int limit = 25,
            Guid? merchantId = null,
            string? orderNo = null,
            string? transactionNo = null,
            string? correlationId = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await operations.SearchAsync(
                new NotificationSearchQuery(page, limit, merchantId, orderNo, transactionNo, correlationId),
                DeliveryAccess(scope), ct));
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new SfsQueryParamsMarker(100))
            .WithTags("การแจ้งเตือน").WithName("ListCanonicalNotifications")
            .WithSummary("ค้นหาผลการแจ้งเตือน")
            .WithDescription("ค้น Notification ตาม Merchant, Order, Transaction หรือ correlation ภายใน Admin scope พร้อมแบ่งหน้า โดยไม่คืน raw recipient หรือ payload")
            .Produces<PagedResult<CommerceNotificationView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/notifications/{notificationId:guid}", async (
            Guid notificationId,
            IAdminScope scope,
            INotificationOperations operations,
            CancellationToken ct) =>
        {
            var result = await operations.GetAsync(notificationId, DeliveryAccess(scope), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การแจ้งเตือน").WithName("GetCanonicalNotification")
            .WithSummary("อ่านผลการแจ้งเตือน")
            .WithDescription("อ่าน Notification และสถานะ delivery ภายใน Merchant scope; recipient ถูก mask และไม่คืน raw payload")
            .Produces<CommerceNotificationView>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/notification-deliveries/{deliveryId:guid}", async (
            Guid deliveryId,
            IAdminScope scope,
            INotificationOperations operations,
            CancellationToken ct) =>
        {
            var result = await operations.GetDeliveryAsync(deliveryId, DeliveryAccess(scope), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การแจ้งเตือน").WithName("GetCanonicalNotificationDelivery")
            .WithSummary("อ่านสถานะ Notification delivery")
            .WithDescription("คืน channel, masked recipient, provider reference และสถานะ ACCEPTED/DELIVERED/UNKNOWN โดยไม่คืน secret หรือ raw payload")
            .Produces<CommerceDeliveryView>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/notification-deliveries/{deliveryId:guid}/attempts", async (
            Guid deliveryId,
            IAdminScope scope,
            INotificationOperations operations,
            CancellationToken ct) =>
        {
            var result = await operations.ListAttemptsAsync(deliveryId, DeliveryAccess(scope), ct);
            return Results.Ok(result);
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การแจ้งเตือน").WithName("ListCanonicalNotificationDeliveryAttempts")
            .WithSummary("รายการความพยายามส่ง Notification")
            .WithDescription("คืน attempt history ที่ redacted ภายใน Merchant scope; ไม่คืน recipient หรือ provider secret")
            .Produces<IReadOnlyList<CommerceDeliveryAttemptView>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static void MapNotificationRetry(RouteGroupBuilder api)
    {
        api.MapPost("/notification-deliveries/{deliveryId:guid}/retries", async (
            Guid deliveryId,
            CanonicalNotificationRetryRequest body,
            HttpContext http,
            IAdminScope scope,
            INotificationOperations operations,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length > 1000)
                throw new InvalidRequestException("Retry reason is invalid.", "validation_failed");
            var result = await operations.RetryAsync(
                deliveryId,
                scope.Current.AdminId,
                body.Reason,
                IdempotencyKeys.Require(http),
                DeliveryAccess(scope),
                ct);
            return result is null ? Results.NotFound() : Results.Accepted(value: result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IdempotencyMutationMarker())
            .WithTags("การแจ้งเตือน").WithName("RetryCanonicalNotificationDelivery")
            .WithSummary("ลองส่ง Notification ใหม่")
            .WithDescription("สร้าง retry จาก delivery snapshot เดิมด้วย Idempotency-Key; delivery ที่สำเร็จแล้วจะไม่ถูกส่งซ้ำ และ attempt history เดิมไม่ถูกแก้")
            .Accepts<CanonicalNotificationRetryRequest>("application/json")
            .Produces<CommerceDeliveryView>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapNotificationReceipt(RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/notifications/{providerCode}", async (
            string providerCode,
            HttpRequest request,
            INotificationReceiptVerifier verifier,
            INotificationOperations operations,
            IActorScope actorScope,
            CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var rawPayload = await reader.ReadToEndAsync(ct);
            var verification = await verifier.VerifyAsync(
                providerCode,
                rawPayload,
                request.Headers["X-Signature"].ToString(),
                ct);
            if (!verification.IsValid)
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = verification.Code ?? "webhook_signature_invalid",
                        ["correlationId"] = request.HttpContext.TraceIdentifier,
                    });
            if (verification.Receipt is null)
                throw new InvalidRequestException("Receipt payload is invalid.", "validation_failed");
            var merchantId = await operations.ResolveDeliveryMerchantAsync(verification.Receipt.DeliveryId, ct);
            if (merchantId is null)
                return Results.NotFound();
            using var actorBinding = actorScope.Begin(merchantId.Value);
            var result = await operations.ApplyReceiptAsync(verification.Receipt, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).AllowAnonymous()
            .WithTags("เว็บฮุก").WithName("HandleNotificationProviderReceipt")
            .WithSummary("Receipt จากผู้ให้บริการ Notification")
            .WithDescription("ตรวจ provider signature/auth ผ่าน provider port แล้ว claim receipt แบบ idempotent; human JWT ไม่ใช่หลักฐาน และ default ที่ไม่มี vendor จะ fail closed")
            .Produces<NotificationReceiptView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static void MapBusinessEventEndpoint(RouteGroupBuilder api)
    {
        api.MapGet("/merchants/{merchantId:guid}/event-endpoint", async (
            Guid merchantId,
            HttpContext http,
            IAdminScope scope,
            IDeliveryControlStore store,
            CancellationToken ct) =>
        {
            EnsureMerchantAccess(scope, merchantId);
            var endpoints = await store.ListEndpointsAsync(
                new WebhookEndpointQuery(1, 100, merchantId, null, null), DeliveryAccess(scope), ct);
            var endpoint = endpoints.Items.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
            if (endpoint is null)
                return Results.NotFound();
            VersionEtags.Set(http, endpoint.Version);
            return Results.Ok(endpoint);
        }).RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("เว็บฮุก").WithName("GetCanonicalMerchantEventEndpoint")
            .WithSummary("อ่าน Merchant event endpoint")
            .WithDescription("คืน endpoint เดียวของ Merchant พร้อม signed-event configuration และ secret hint เท่านั้น ไม่คืน signing credential")
            .Produces<WebhookEndpointView>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/merchants/{merchantId:guid}/event-endpoint", async (
            Guid merchantId,
            CanonicalEventEndpointRequest body,
            HttpContext http,
            IAdminScope scope,
            IDeliveryControlStore store,
            CancellationToken ct) =>
        {
            EnsureMerchantAccess(scope, merchantId);
            ValidateSigningKeyReference(body.SigningKeyReference);
            if (!body.Enabled && body.Url is not null)
                throw new InvalidRequestException("Disabled event endpoint must omit URL.", "validation_failed");
            var access = DeliveryAccess(scope);
            var endpoints = await store.ListEndpointsAsync(
                new WebhookEndpointQuery(1, 100, merchantId, null, null), access, ct);
            var current = endpoints.Items.OrderByDescending(x => x.UpdatedAt).FirstOrDefault();
            var key = IdempotencyKeys.Require(http);
            if (current is null)
            {
                if (!body.Enabled)
                    return Results.NotFound();
                if (string.IsNullOrWhiteSpace(body.Url))
                    throw new InvalidRequestException("Enabled event endpoint requires URL.", "validation_failed");
                var created = await store.CreateEndpointAsync(
                    merchantId,
                    "business-events",
                    body.Url,
                    BusinessEvents,
                    scope.Current.AdminId,
                    key,
                    access,
                    ct);
                return Results.Ok(created.Endpoint);
            }

            var updated = await store.UpdateEndpointAsync(
                current.Id,
                current.Name,
                body.Enabled ? body.Url ?? current.Url : body.Url ?? current.Url,
                BusinessEvents,
                body.Enabled,
                VersionEtags.Require(http),
                scope.Current.AdminId,
                key,
                access,
                ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated.Endpoint);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("เว็บฮุก").WithName("PutCanonicalMerchantEventEndpoint")
            .WithSummary("กำหนดหรือปิด Merchant event endpoint")
            .WithDescription("ใช้ destination HTTPS/443 ที่ผ่าน SSRF-safe resolver, signed events และ endpoint เดียวต่อ Merchant; `url=null` ใช้ได้เมื่อปิด และห้ามมี credential ใน URL")
            .Accepts<CanonicalEventEndpointRequest>("application/json")
            .Produces<WebhookEndpointView>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);
    }

    private static void MapAuditLogs(RouteGroupBuilder api)
    {
        api.MapGet("/audit-logs", async (
            IAdminScope scope,
            IGovernanceStore store,
            int page = 1,
            int limit = 25,
            Guid? actor = null,
            string? action = null,
            string? resource = null,
            string? result = null,
            Guid? merchantId = null,
            DateTime? from = null,
            DateTime? to = null,
            CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            if (from > to)
                throw new InvalidRequestException("Audit time range is invalid.", "invalid_filter");
            try
            {
                return Results.Ok(await store.ListAuditsAsync(new AuditQuery(
                    page, limit, actor, action, resource, result, merchantId, from, to, GovernanceAccess(scope)), ct));
            }
            catch (AuditIntegrityException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    extensions: new Dictionary<string, object?> { ["code"] = "audit_integrity_unhealthy" });
            }
        }).RequireAuthorization("admin").RequirePermission(Keys.AuditView)
            .WithMetadata(new SfsQueryParamsMarker(100))
            .WithTags("บันทึกการตรวจสอบ").WithName("ListCanonicalAuditLogs")
            .WithSummary("ค้น audit logs")
            .WithDescription("คืน append-only audit ภายใน Merchant scope พร้อม actor/target/correlation และ safe fields; ไม่คืน raw secret หรือ PII")
            .Produces<PagedResult<AuditListItem>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
    }

    private static DeliveryAccess DeliveryAccess(IAdminScope scope) =>
        new(scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

    private static GovernanceAccess GovernanceAccess(IAdminScope scope)
    {
        var current = scope.Current;
        return new GovernanceAccess(
            current.AdminId,
            current.Accessible.IsUnrestricted,
            current.Accessible.Merchants,
            current.Permissions);
    }

    private static void EnsureMerchantAccess(IAdminScope scope, Guid merchantId)
    {
        if (merchantId == Guid.Empty || !scope.Accessible.Allows(merchantId))
            throw new AccessDeniedException("Merchant is outside the current Admin scope.", "merchant_scope_forbidden");
    }

    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }

    private static void ValidateSigningKeyReference(string? value)
    {
        if (value is null)
            return;
        if (value.Length > 200 || value.Any(char.IsControl))
            throw new InvalidRequestException("Signing key reference is invalid.", "validation_failed");
    }
}

internal sealed record CanonicalNotificationRetryRequest(string Reason);
internal sealed record CanonicalEventEndpointRequest(string? Url, bool Enabled, string? SigningKeyReference);
