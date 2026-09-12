using System.ComponentModel.DataAnnotations;
using Admins.Application;
using BuildingBlocks.Application;
using Iam.Application.ApiClients;
using Iam.Domain.Permissions;

namespace Api.Iam;

internal static class ApiClientEndpoints
{
    public static void MapApiClientEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/api-clients", async (IAdminScope scope, IApiClientStore store,
            int page = 1, int limit = 25, string? search = null, Guid? merchantId = null,
            string? status = null, CancellationToken ct = default) =>
        {
            ValidatePage(page, limit);
            return Results.Ok(await store.ListAsync(Access(scope), page, limit, search, merchantId, status, ct));
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.ApiKeyManage)
            .WithTags("ไคลเอนต์ API").WithName("ListApiClients")
            .WithSummary("รายการ API client")
            .WithDescription("คืน API client ภายใน merchant scope แบบแบ่งหน้า กรอง merchantId/status และค้นจาก name หรือ clientId ได้ ไม่คืน client secret")
            .Produces<PagedResult<ApiClientView>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        api.MapGet("/api-clients/{clientId:guid}", async (Guid clientId, HttpContext http,
            IAdminScope scope, IApiClientStore store, CancellationToken ct) =>
        {
            var result = await store.GetAsync(clientId, Access(scope), ct);
            if (result is null) return Results.NotFound();
            VersionEtags.Set(http, result.Version);
            return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.ApiKeyManage)
            .WithMetadata(new EtagResponseMarker("200"))
            .WithTags("ไคลเอนต์ API").WithName("GetApiClient")
            .WithSummary("อ่าน API client")
            .WithDescription("คืน config, scope, status, secret hint และ ETag โดยไม่คืน client secret หากไม่พบหรือนอก merchant scope -> 404")
            .Produces<ApiClientView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        api.MapPost("/api-clients", async (CreateApiClientRequest body, HttpContext http,
            IAdminScope scope, IApiClientStore store, CancellationToken ct) =>
        {
            var result = await store.CreateAsync(new(body.Name, body.MerchantId, body.OriginatorId,
                body.Scopes ?? [], body.IpPolicy, scope.Current.AdminId, IdempotencyKeys.Require(http)), Access(scope), ct);
            http.Response.Headers.CacheControl = "no-store";
            VersionEtags.Set(http, result.Client.Version);
            return Results.Created($"/api/v1/api-clients/{result.Client.Id:D}", result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.ApiKeyManage)
            .WithMetadata(new IdempotencyMutationMarker(), new EtagResponseMarker("201"))
            .WithTags("ไคลเอนต์ API").WithName("CreateApiClient")
            .WithSummary("สร้าง API client")
            .WithDescription("สร้าง client ภายใน merchant scope และคืน one-time secret ticket ไม่คืน plaintext secret โดยตรง ต้องส่ง Idempotency-Key; scope หรือ IP policy ไม่ถูกต้อง -> 400")
            .Produces<ApiClientCreated>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409);

        api.MapPut("/api-clients/{clientId:guid}", async (Guid clientId, UpdateApiClientRequest body,
            HttpContext http, IAdminScope scope, IApiClientStore store, CancellationToken ct) =>
        {
            var result = await store.UpdateAsync(new(clientId, body.Name, body.Scopes ?? [], body.IpPolicy,
                VersionEtags.Require(http), scope.Current.AdminId, IdempotencyKeys.Require(http)), Access(scope), ct);
            if (result is null) return Results.NotFound();
            VersionEtags.Set(http, result.Client.Version);
            return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.ApiKeyManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ไคลเอนต์ API").WithName("UpdateApiClient")
            .WithSummary("แก้ไข API client")
            .WithDescription("แก้ name, scopes และ IP policy โดยต้องส่ง If-Match กับ Idempotency-Key ไม่เปลี่ยน clientId หรือ secret หากไม่พบ -> 404, version/idempotency ชนกัน -> 409")
            .Produces<ApiClientMutation>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapPost("/api-clients/{clientId:guid}/revoke", async (Guid clientId, HttpContext http,
            IAdminScope scope, IApiClientStore store, CancellationToken ct) =>
        {
            var result = await store.RevokeAsync(clientId, VersionEtags.Require(http), scope.Current.AdminId,
                IdempotencyKeys.Require(http), Access(scope), ct);
            if (result is null) return Results.NotFound();
            VersionEtags.Set(http, result.Client.Version);
            return Results.Ok(result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.ApiKeyManage)
            .WithMetadata(new IfMatchMutationMarker("200"), new IdempotencyMutationMarker())
            .WithTags("ไคลเอนต์ API").WithName("RevokeApiClient")
            .WithSummary("เพิกถอน API client")
            .WithDescription("เปลี่ยน API client เป็น revoked โดยต้องส่ง If-Match กับ Idempotency-Key หลังสำเร็จใช้ยืนยันตัวตนไม่ได้")
            .Produces<ApiClientMutation>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapPost("/api-clients/{clientId:guid}/secret-rotation-requests", async (
            Guid clientId, HttpContext http, IAdminScope scope, IApiClientStore store, CancellationToken ct) =>
        {
            var result = await store.RequestRotationAsync(
                clientId, VersionEtags.Require(http), scope.Current.AdminId, IdempotencyKeys.Require(http),
                http.TraceIdentifier, Access(scope), ct);
            if (result is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            VersionEtags.Set(http, result.ClientVersion);
            return Results.Accepted($"/api/v1/approvals/{result.ApprovalId:D}", result);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.ApiKeyManage)
            .WithMetadata(new IfMatchMutationMarker("202"), new IdempotencyMutationMarker())
            .WithTags("ไคลเอนต์ API").WithName("RequestApiClientSecretRotation")
            .WithSummary("ขอหมุน client secret")
            .WithDescription("สร้างคำขอ maker-checker และ one-time reveal ticket สถานะ pending โดยต้องส่ง If-Match กับ Idempotency-Key secret ใหม่เปิดดูได้หลังคำขออนุมัติ")
            .Produces<ApiClientRotationRequested>(202).ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);

        api.MapPost("/api-clients/secrets/{ticketId}/reveal", async (string ticketId, HttpContext http,
            IApiClientStore store, CancellationToken ct) =>
        {
            _ = IdempotencyKeys.Require(http);
            var result = await store.RevealAsync(ticketId, ct);
            if (result.State == SecretRevealState.Pending)
                return Results.Problem(statusCode: 409,
                    extensions: new Dictionary<string, object?> { ["code"] = "secret_ticket_pending" });
            if (result.State == SecretRevealState.Consumed)
                return Results.Problem(statusCode: 410,
                    extensions: new Dictionary<string, object?> { ["code"] = "secret_ticket_consumed" });
            if (result.State == SecretRevealState.Rejected)
                return Results.Problem(statusCode: 410,
                    extensions: new Dictionary<string, object?> { ["code"] = "secret_ticket_rejected" });
            if (result.State is SecretRevealState.Expired or SecretRevealState.Unknown)
                return Results.Problem(statusCode: 410,
                    extensions: new Dictionary<string, object?> { ["code"] = "secret_ticket_expired" });
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            return Results.Ok(result.Secret);
        }).RequireCsrf().RequireAuthorization("admin").RequirePermission(Keys.ApiKeyManage)
            .WithMetadata(new IdempotencyMutationMarker())
            .WithTags("ไคลเอนต์ API").WithName("RevealApiClientSecret")
            .WithSummary("เปิดดู client secret หนึ่งครั้ง")
            .WithDescription("consume one-time ticket แล้วคืน clientId กับ clientSecret พร้อม Cache-Control no-store ticket ที่ pending -> 409; ใช้แล้ว, ถูกปฏิเสธ, หมดอายุ หรือไม่รู้จัก -> 410")
            .Produces<SecretReveal>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(410);
    }

    private static ApiClientAccess Access(IAdminScope scope) =>
        new(scope.Accessible.IsUnrestricted, scope.Accessible.Merchants);
    private static void ValidatePage(int page, int limit)
    {
        if (page < 1 || limit is < 1 or > 100)
            throw new InvalidRequestException("Page and limit are invalid.", "invalid_filter");
    }
}

internal sealed record CreateApiClientRequest([property: Required] string Name, Guid MerchantId,
    Guid? OriginatorId, IReadOnlyList<string>? Scopes, string? IpPolicy);
internal sealed record UpdateApiClientRequest([property: Required] string Name,
    IReadOnlyList<string>? Scopes, string? IpPolicy);
