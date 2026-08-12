using System.ComponentModel.DataAnnotations;
using Admins.Application;
using Api.Iam;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Notifications.Application;

namespace Api.Notifications;

internal static class DeliveryEndpoints
{
    public static void MapDeliveryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/webhooks/endpoints", async (IAdminScope scope, IDeliveryControlStore store,
            int page = 1, int limit = 25, Guid? merchantId = null, bool? enabled = null,
            string? search = null, CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListEndpointsAsync(
                new(page, limit, merchantId, enabled, search), Access(scope), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("เว็บฮุก").WithName("ListWebhookEndpoints")
            .WithSummary("รายการ outbound webhook endpoint")
            .WithDescription("คืน endpoint ภายใน merchant scope แบบแบ่งหน้า กรอง merchantId/enabled และค้นจาก name, URL หรือ event ได้ ไม่คืน signing secret")
            .Produces<PagedResult<WebhookEndpointView>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        api.MapGet("/webhooks/endpoints/{endpointId:guid}", async (Guid endpointId, HttpContext http,
            IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.GetEndpointAsync(endpointId, Access(scope), ct);
            if (result is null) return Results.NotFound();
            VersionEtags.Set(http, result.Version); return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("เว็บฮุก").WithName("GetWebhookEndpoint")
            .WithSummary("อ่าน outbound webhook endpoint")
            .WithDescription("คืน URL, subscribed events, enabled, secret hint และ ETag โดยไม่คืน signing secret หากไม่พบหรือนอก merchant scope -> 404")
            .Produces<WebhookEndpointView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        api.MapPost("/webhooks/endpoints", async (WebhookEndpointRequest body, HttpContext http,
            IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.CreateEndpointAsync(body.MerchantId, body.Name, body.Url,
                body.Events ?? [], scope.Current.AdminId, IdempotencyKeys.Require(http), Access(scope), ct);
            http.Response.Headers.CacheControl = "no-store"; VersionEtags.Set(http, result.Endpoint.Version);
            return Results.Created($"/api/v1/webhooks/endpoints/{result.Endpoint.Id:D}", result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithTags("เว็บฮุก").WithName("CreateWebhookEndpoint")
            .WithSummary("สร้าง outbound webhook endpoint")
            .WithDescription("ตรวจ URL ด้วย SSRF-safe resolver ก่อนบันทึก และคืน signing secret เฉพาะ response แรกพร้อม no-store ต้องส่ง Idempotency-Key; replay ไม่คืน secret ซ้ำ")
            .Produces<WebhookEndpointCreated>(201).ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(409);

        api.MapPut("/webhooks/endpoints/{endpointId:guid}", async (Guid endpointId,
            WebhookEndpointUpdateRequest body, HttpContext http, IAdminScope scope,
            IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.UpdateEndpointAsync(endpointId, body.Name, body.Url, body.Events ?? [],
                body.Enabled, VersionEtags.Require(http), scope.Current.AdminId, IdempotencyKeys.Require(http),
                Access(scope), ct);
            if (result is null) return Results.NotFound();
            VersionEtags.Set(http, result.Endpoint.Version); return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("เว็บฮุก").WithName("UpdateWebhookEndpoint")
            .WithSummary("แก้ไข outbound webhook endpoint")
            .WithDescription("แก้ name, URL, events และ enabled หลังตรวจ destination โดยต้องส่ง If-Match กับ Idempotency-Key ไม่หมุน signing secret")
            .Produces<WebhookEndpointMutation>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapDelete("/webhooks/endpoints/{endpointId:guid}", async (Guid endpointId,
            HttpContext http, IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.DeleteEndpointAsync(endpointId, VersionEtags.Require(http),
                scope.Current.AdminId, IdempotencyKeys.Require(http), Access(scope), ct);
            return result is null ? Results.NotFound() : Results.NoContent();
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("204"), new IdempotencyMutationMarker())
            .WithTags("เว็บฮุก").WithName("DeleteWebhookEndpoint")
            .WithSummary("ลบ outbound webhook endpoint")
            .WithDescription("ลบ endpoint และ retire secret โดยต้องส่ง If-Match กับ Idempotency-Key endpoint ที่มี delivery history ลบไม่ได้ -> 409")
            .Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapGet("/webhooks/deliveries", async (IAdminScope scope, IDeliveryControlStore store,
            int page = 1, int limit = 25, Guid? merchantId = null, string? status = null,
            string? search = null, CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListWebhookDeliveriesAsync(
                new(page, limit, merchantId, status, search), Access(scope), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("เว็บฮุก").WithName("ListWebhookDeliveries")
            .WithSummary("รายการ outbound webhook delivery")
            .WithDescription("คืน delivery ภายใน merchant scope แบบแบ่งหน้า กรอง merchantId/status และค้นจาก eventType หรือ transactionId ได้ ไม่คืน raw payload หรือ signature")
            .Produces<PagedResult<WebhookDeliveryView>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        api.MapGet("/webhooks/deliveries/{deliveryId:guid}", async (Guid deliveryId,
            IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.GetWebhookDeliveryAsync(deliveryId, Access(scope), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("เว็บฮุก").WithName("GetWebhookDelivery")
            .WithSummary("อ่าน outbound webhook delivery")
            .WithDescription("คืนสถานะ, attempt count, latency, failure code และ replay eligibility โดยไม่คืน raw payload หรือ signature หากไม่พบ -> 404")
            .Produces<WebhookDeliveryView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        api.MapPost("/webhooks/deliveries/{deliveryId:guid}/replay", async (Guid deliveryId,
            HttpContext http, IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.ReplayAsync(deliveryId, scope.Current.AdminId,
                IdempotencyKeys.Require(http), Access(scope), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IdempotencyMutationMarker())
            .WithTags("เว็บฮุก").WithName("ReplayWebhookDelivery")
            .WithSummary("ส่ง outbound webhook ซ้ำ")
            .WithDescription("สร้าง delivery ใหม่จากรายการที่ replay ได้ โดยไม่แก้ประวัติเดิม ต้องส่ง Idempotency-Key หากไม่เข้าเงื่อนไข -> 409")
            .Produces<WebhookReplayResult>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapGet("/notifications/rules", async (IAdminScope scope, IDeliveryControlStore store,
            int page = 1, int limit = 25, Guid? merchantId = null, bool? enabled = null,
            string? search = null, CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListRulesAsync(
                new(page, limit, merchantId, enabled, search), Access(scope), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การแจ้งเตือน").WithName("ListNotificationRules")
            .WithSummary("รายการกฎการแจ้งเตือน")
            .WithDescription("คืนกฎภายใน merchant scope แบบแบ่งหน้า กรอง merchantId/enabled และค้นจาก eventType หรือ channel ได้")
            .Produces<PagedResult<NotificationRuleView>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        api.MapGet("/notifications/rules/{ruleId:guid}", async (Guid ruleId, HttpContext http,
            IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.GetRuleAsync(ruleId, Access(scope), ct);
            if (result is null) return Results.NotFound();
            VersionEtags.Set(http, result.Version); return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("การแจ้งเตือน").WithName("GetNotificationRule")
            .WithSummary("อ่านกฎการแจ้งเตือน")
            .WithDescription("คืน eventType, channel, destination, threshold, enabled และ ETag หากไม่พบหรือนอก merchant scope -> 404")
            .Produces<NotificationRuleView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        api.MapPost("/notifications/rules", async (NotificationRuleRequest body, HttpContext http,
            IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.CreateRuleAsync(body.MerchantId, body.EventType, body.Channel,
                body.Destination, body.Threshold, body.Enabled, scope.Current.AdminId,
                IdempotencyKeys.Require(http), Access(scope), ct);
            VersionEtags.Set(http, result.Rule.Version);
            return Results.Created($"/api/v1/notifications/rules/{result.Rule.Id:D}", result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithTags("การแจ้งเตือน").WithName("CreateNotificationRule")
            .WithSummary("สร้างกฎการแจ้งเตือน")
            .WithDescription("สร้างกฎ event/channel/destination ภายใน merchant scope โดยต้องส่ง Idempotency-Key ค่าไม่ถูกต้อง -> 400")
            .Produces<NotificationRuleMutation>(201).ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(409);

        api.MapPut("/notifications/rules/{ruleId:guid}", async (Guid ruleId,
            NotificationRuleUpdateRequest body, HttpContext http, IAdminScope scope,
            IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.UpdateRuleAsync(ruleId, body.EventType, body.Channel,
                body.Destination, body.Threshold, body.Enabled, VersionEtags.Require(http),
                scope.Current.AdminId, IdempotencyKeys.Require(http), Access(scope), ct);
            if (result is null) return Results.NotFound();
            VersionEtags.Set(http, result.Rule.Version); return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("การแจ้งเตือน").WithName("UpdateNotificationRule")
            .WithSummary("แก้ไขกฎการแจ้งเตือน")
            .WithDescription("แทนค่า eventType, channel, destination, threshold และ enabled โดยต้องส่ง If-Match กับ Idempotency-Key")
            .Produces<NotificationRuleMutation>().ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapDelete("/notifications/rules/{ruleId:guid}", async (Guid ruleId,
            HttpContext http, IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.DeleteRuleAsync(ruleId, VersionEtags.Require(http),
                scope.Current.AdminId, IdempotencyKeys.Require(http), Access(scope), ct);
            return result is null ? Results.NotFound() : Results.NoContent();
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithMetadata(new IfMatchMutationMarker("204"), new IdempotencyMutationMarker())
            .WithTags("การแจ้งเตือน").WithName("DeleteNotificationRule")
            .WithSummary("ลบกฎการแจ้งเตือน")
            .WithDescription("ลบกฎโดยต้องส่ง If-Match กับ Idempotency-Key กฎที่มี delivery history ลบไม่ได้ -> 409")
            .Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapGet("/notifications/deliveries", async (IAdminScope scope, IDeliveryControlStore store,
            int page = 1, int limit = 25, Guid? merchantId = null, string? channel = null, string? status = null,
            string? search = null, CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListNotificationDeliveriesAsync(
                new(page, limit, merchantId, channel, status, search), Access(scope), ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การแจ้งเตือน").WithName("ListNotificationDeliveries")
            .WithSummary("รายการ notification delivery")
            .WithDescription("คืนประวัติการส่งภายใน merchant scope แบบแบ่งหน้า กรอง merchantId/channel/status และค้นจาก eventType หรือ destination ได้")
            .Produces<PagedResult<NotificationDeliveryView>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        api.MapGet("/notifications/deliveries/{deliveryId:guid}", async (Guid deliveryId,
            IAdminScope scope, IDeliveryControlStore store, CancellationToken ct) =>
        {
            var result = await store.GetNotificationDeliveryAsync(deliveryId, Access(scope), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.SettingsManage)
            .WithTags("การแจ้งเตือน").WithName("GetNotificationDelivery")
            .WithSummary("อ่าน notification delivery")
            .WithDescription("คืน eventType, channel, destination, status, failure code และเวลาส่ง หากไม่พบหรือนอก merchant scope -> 404")
            .Produces<NotificationDeliveryView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }

    private static DeliveryAccess Access(IAdminScope scope) =>
        new(scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);

    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }
}

internal sealed record WebhookEndpointRequest(Guid MerchantId, [property: Required] string Name,
    [property: Required] string Url, IReadOnlyList<string>? Events);
internal sealed record WebhookEndpointUpdateRequest([property: Required] string Name,
    [property: Required] string Url, IReadOnlyList<string>? Events, bool Enabled);
internal sealed record NotificationRuleRequest(Guid MerchantId, [property: Required] string EventType,
    [property: Required] string Channel, [property: Required] string Destination,
    string? Threshold, bool Enabled);
internal sealed record NotificationRuleUpdateRequest([property: Required] string EventType,
    [property: Required] string Channel, [property: Required] string Destination,
    string? Threshold, bool Enabled);
