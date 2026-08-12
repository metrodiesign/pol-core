using Admins.Application;
using Api.Iam;
using BuildingBlocks.Application;
using Iam.Domain.Permissions;
using Payments.Application.AdminControlPlane;

namespace Api.Webhooks;

internal static class InboundWebhookEndpoints
{
    public static void MapInboundWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/webhooks/inbound-events", async (
            IAdminScope scope,
            IAdminInboundWebhookReader reader,
            int page = 1,
            int limit = 25,
            Guid? merchantId = null,
            string? psp = null,
            string? status = null,
            string? search = null,
            DateTime? from = null,
            DateTime? to = null,
            CancellationToken ct = default) =>
        {
            try
            {
                return Results.Ok(await reader.ListAsync(new InboundWebhookQuery(
                    page, limit, merchantId, psp, status, search, from, to, Access(scope)), ct));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidRequestException(ex.Message, "invalid_filter");
            }
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.AuditView)
            .WithTags("เว็บฮุก")
            .WithName("ListInboundWebhookEvents")
            .WithSummary("รายการ PSP callback ที่ผ่านการลดข้อมูลอ่อนไหวแล้ว")
            .WithDescription("คืน inbound PSP events ภายใน merchant scope แบบแบ่งหน้า กรอง merchantId, psp, status, ช่วงเวลา และค้นหาได้ เก็บเฉพาะ payload fingerprint ไม่คืน raw payload หรือลายเซ็น")
            .Produces<PagedResult<InboundWebhookEventView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapGet("/webhooks/inbound-events/{eventId:guid}", async (
            Guid eventId,
            IAdminScope scope,
            IAdminInboundWebhookReader reader,
            CancellationToken ct) =>
        {
            var result = await reader.GetAsync(eventId, Access(scope), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.AuditView)
            .WithTags("เว็บฮุก")
            .WithName("GetInboundWebhookEvent")
            .WithSummary("รายละเอียด PSP callback โดยไม่คืน raw payload หรือลายเซ็น")
            .WithDescription("คืน connection, merchant, order/session linkage, payload fingerprint, signature result, status และเวลาประมวลผล หากไม่พบหรือนอก merchant scope -> 404")
            .Produces<InboundWebhookEventView>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static InboundWebhookAccess Access(IAdminScope scope) =>
        new(scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);
}
